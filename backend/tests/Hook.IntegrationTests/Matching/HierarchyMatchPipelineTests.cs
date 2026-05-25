using Hook.Features.Matching.Match;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Location = Hook.Features.Geocoding.Models.Location;

namespace Hook.IntegrationTests.Matching;

[Collection("Pipeline-4")]
public sealed class HierarchyMatchPipelineTests : PipelineTestBase
{
    public HierarchyMatchPipelineTests(DevPipelineFixture fx) : base(fx) { }

    // Stable per-call phone suffix — Random.Shared.Next is a shard-flake vector.
    // Pipeline-4 serializes tests within the collection; a process-wide counter
    // is sufficient since the DB is truncated between [Fact]s.
    private static int _phoneCounter;
    private static string NextPhone() =>
        $"+22070000{Interlocked.Increment(ref _phoneCounter):D2}";

    [Fact]
    public async Task FindCandidates_NarrowedKind_WhenProviderHasChildSlug()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var query = scope.ServiceProvider.GetRequiredService<IProviderQueryService>();

        // RootSectorSeeder already populated "software-engineering" at host boot —
        // only seed the child slug here.
        var childSlug = $"net-dev-{Guid.NewGuid():N}";
        var child = Service.Create(childSlug, DateTimeOffset.UtcNow);
        db.Services.Add(child);
        await db.SaveChangesAsync();
        var parent = await db.Services.FindAsync("software-engineering");
        child.AssignParent(parent!);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var location = new Location(DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        var provider = ProviderAvailability.Register(
            NextPhone(),
            [childSlug], location, "Banjul", shareContact: true,
            ttl: TimeSpan.FromHours(1), now);
        db.ProviderAvailabilities.Add(provider);
        await db.SaveChangesAsync();

        // Client asks for the PARENT — query narrows to the child specialist.
        var expanded = new ExpandedSlugs("software-engineering", Parent: null, Children: [childSlug]);
        var scored = await query.FindCandidatesAsync(
            location.ToPoint(), expanded, radiusKm: 5, excludePhones: [], now);

        var match = scored.ShouldHaveSingleItem();
        match.Candidate.Phone.ShouldBe(provider.Phone);
        match.Kind.ShouldBe(MatchKind.Narrowed);
    }

    [Fact]
    public async Task FindCandidates_BroadenedKind_WhenProviderHasParentSlug()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var query = scope.ServiceProvider.GetRequiredService<IProviderQueryService>();

        // Seed: doctor (root) + cardiology (child of doctor).
        var childSlug = $"cardio-{Guid.NewGuid():N}";
        db.Services.Add(Service.Create(childSlug, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var child = await db.Services.FindAsync(childSlug);
        var parent = await db.Services.FindAsync("doctor");
        child!.AssignParent(parent!);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var location = new Location(DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        var generalist = ProviderAvailability.Register(
            NextPhone(),
            ["doctor"], location, "Banjul", shareContact: true,
            ttl: TimeSpan.FromHours(1), now);
        db.ProviderAvailabilities.Add(generalist);
        await db.SaveChangesAsync();

        // Client asks for the CHILD — query broadens to the generalist parent.
        var expanded = new ExpandedSlugs(childSlug, Parent: "doctor", Children: Array.Empty<string>());
        var scored = await query.FindCandidatesAsync(
            location.ToPoint(), expanded, radiusKm: 5, excludePhones: [], now);

        var match = scored.ShouldHaveSingleItem();
        match.Candidate.Phone.ShouldBe(generalist.Phone);
        match.Kind.ShouldBe(MatchKind.Broadened);
    }

    [Fact]
    public async Task FindCandidates_ExactKind_WhenProviderHasRequestedSlug()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var query = scope.ServiceProvider.GetRequiredService<IProviderQueryService>();

        var now = DateTimeOffset.UtcNow;
        var location = new Location(DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        var exactProvider = ProviderAvailability.Register(
            NextPhone(),
            ["plumbing"], location, "Banjul", shareContact: true,
            ttl: TimeSpan.FromHours(1), now);
        db.ProviderAvailabilities.Add(exactProvider);
        await db.SaveChangesAsync();

        var expanded = new ExpandedSlugs("plumbing", Parent: null, Children: Array.Empty<string>());
        var scored = await query.FindCandidatesAsync(
            location.ToPoint(), expanded, radiusKm: 5, excludePhones: [], now);

        scored.ShouldContain(s => s.Candidate.Phone == exactProvider.Phone && s.Kind == MatchKind.Exact);
    }
}
