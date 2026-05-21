using Hook.Features.Matching;
using Hook.Features.Matching.Match;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Location = Hook.Features.Geocoding.Models.Location;

namespace Hook.IntegrationTests.Matching;

[Collection("Pipeline-4")]
public sealed class PostgresProviderQueryServiceTests : PipelineTestBase
{
    public PostgresProviderQueryServiceTests(DevPipelineFixture fx) : base(fx) { }

    private const double LatDegPerKm = 1.0 / 110.574;
    private static int _phoneSeq;
    private static string NextPhone() => $"+22070{Interlocked.Increment(ref _phoneSeq):D6}";

    [Fact]
    public async Task FindCandidates_AcrossHierarchyBranches_MergesTopKByDistance()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var query = scope.ServiceProvider.GetRequiredService<IProviderQueryService>();
        var maxK = scope.ServiceProvider.GetRequiredService<IOptions<MatchingOptions>>().Value.MaxCandidatePoolSize;

        var requestedSlug = $"req-{Guid.NewGuid():N}";
        var parentSlug = $"par-{Guid.NewGuid():N}";
        var childSlug = $"chi-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        var requestLocation = new Location(DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);

        // 30 distinct providers across the three branches with strictly-increasing
        // distance, so the expected top-K is the smallest 10 phones — fully
        // deterministic and immune to per-branch ordering races.
        var expected = new List<(string Phone, double NorthKm)>();
        for (var i = 0; i < 30; i++)
        {
            var phone = $"+2207000{i:D4}";
            var northKm = 0.05 + i * 0.05;
            var slug = (i % 3) switch
            {
                0 => requestedSlug,
                1 => parentSlug,
                _ => childSlug,
            };
            db.ProviderAvailabilities.Add(ProviderAvailability.Register(
                phone, [slug],
                new Location(requestLocation.Latitude + northKm * LatDegPerKm, requestLocation.Longitude),
                "Banjul", shareContact: true,
                ttl: TimeSpan.FromHours(1), now));
            expected.Add((phone, northKm));
        }
        await db.SaveChangesAsync();

        var slugs = new ExpandedSlugs(requestedSlug, parentSlug, [childSlug]);
        var result = await query.FindCandidatesAsync(
            requestLocation.ToPoint(), slugs, radiusKm: 50, excludePhones: [], now);

        result.Count.ShouldBeLessThanOrEqualTo(maxK);
        result.Count.ShouldBe(expected.Count);

        var resultPhonesByDistance = result.Select(r => r.Candidate.Phone).ToList();
        var expectedPhonesByDistance = expected.OrderBy(e => e.NorthKm).Select(e => e.Phone).ToList();
        resultPhonesByDistance.ShouldBe(expectedPhonesByDistance);

        var distances = result.Select(r => r.Candidate.DistanceKm).ToList();
        distances.ShouldBe(distances.OrderBy(d => d).ToList());
    }

    [Fact]
    public async Task FindCandidates_HonoursPerBranchLimitAndOverallTopK()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var query = scope.ServiceProvider.GetRequiredService<IProviderQueryService>();
        var maxK = scope.ServiceProvider.GetRequiredService<IOptions<MatchingOptions>>().Value.MaxCandidatePoolSize;

        var slug = $"cap-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var requestLocation = new Location(DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);

        var totalProviders = maxK + 10;
        for (var i = 0; i < totalProviders; i++)
        {
            var phone = $"+2207100{i:D5}";
            var northKm = 0.05 + i * 0.05;
            db.ProviderAvailabilities.Add(ProviderAvailability.Register(
                phone, [slug],
                new Location(requestLocation.Latitude + northKm * LatDegPerKm, requestLocation.Longitude),
                "Banjul", shareContact: true,
                ttl: TimeSpan.FromHours(1), now));
        }
        await db.SaveChangesAsync();

        var slugs = new ExpandedSlugs(slug, Parent: null, Children: []);
        var result = await query.FindCandidatesAsync(
            requestLocation.ToPoint(), slugs, radiusKm: 100, excludePhones: [], now);

        result.Count.ShouldBe(maxK);
        var distances = result.Select(r => r.Candidate.DistanceKm).ToList();
        distances.ShouldBe(distances.OrderBy(d => d).ToList());
    }

    [Fact]
    public async Task FindCandidates_MultiBranch_PerBranchLimitFloor_PreservesTopK()
    {
        // Per-branch K is now max(50, MaxK / branchCount). With 4 branches and the
        // default MaxK=200, per-branch K = max(50, 50) = 50. Seed 60 per branch so
        // each branch is over the per-branch limit; verify the merge still produces
        // the globally-closest MaxK.
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var query = scope.ServiceProvider.GetRequiredService<IProviderQueryService>();
        var maxK = scope.ServiceProvider.GetRequiredService<IOptions<MatchingOptions>>().Value.MaxCandidatePoolSize;

        var slugs = new[]
        {
            $"mb-req-{Guid.NewGuid():N}",
            $"mb-par-{Guid.NewGuid():N}",
            $"mb-ch1-{Guid.NewGuid():N}",
            $"mb-ch2-{Guid.NewGuid():N}",
        };
        var now = DateTimeOffset.UtcNow;
        var requestLocation = new Location(DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);

        // 60 distinct phones per branch, interleaved so the global top-MaxK is a
        // mix from every branch (not concentrated in one). Each provider gets one
        // slug from the four (no cross-branch overlap), so GroupBy(Phone).First()
        // is exercised but dedup is not the focus here.
        const int perBranchCount = 60;
        var totalRows = perBranchCount * slugs.Length;
        for (var i = 0; i < totalRows; i++)
        {
            var slug = slugs[i % slugs.Length];
            var phone = $"+220720{i:D5}";
            var northKm = 0.05 + i * 0.01;
            db.ProviderAvailabilities.Add(ProviderAvailability.Register(
                phone, [slug],
                new Location(requestLocation.Latitude + northKm * LatDegPerKm, requestLocation.Longitude),
                "Banjul", shareContact: true, ttl: TimeSpan.FromHours(1), now));
        }
        await db.SaveChangesAsync();

        var expanded = new ExpandedSlugs(slugs[0], slugs[1], [slugs[2], slugs[3]]);
        var result = await query.FindCandidatesAsync(
            requestLocation.ToPoint(), expanded, radiusKm: 50, excludePhones: [], now);

        result.Count.ShouldBe(maxK);
        var distances = result.Select(r => r.Candidate.DistanceKm).ToList();
        distances.ShouldBe(distances.OrderBy(d => d).ToList());
    }

    [Fact]
    public async Task FindCandidates_DedupesProviderListedUnderMultipleBranches()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var query = scope.ServiceProvider.GetRequiredService<IProviderQueryService>();

        var requestedSlug = $"req-{Guid.NewGuid():N}";
        var parentSlug = $"par-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var location = new Location(DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        var phone = NextPhone();

        db.ProviderAvailabilities.Add(ProviderAvailability.Register(
            phone, [requestedSlug, parentSlug], location, "Banjul", shareContact: true,
            ttl: TimeSpan.FromHours(1), now));
        await db.SaveChangesAsync();

        var slugs = new ExpandedSlugs(requestedSlug, parentSlug, []);
        var result = await query.FindCandidatesAsync(
            location.ToPoint(), slugs, radiusKm: 5, excludePhones: [], now);

        var single = result.ShouldHaveSingleItem();
        single.Candidate.Phone.ShouldBe(phone);
    }
}
