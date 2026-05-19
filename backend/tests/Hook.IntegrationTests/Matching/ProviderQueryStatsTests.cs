using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Features.Matching.Match;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.Extensions.DependencyInjection;
using Location = Hook.Features.Geocoding.Models.Location;

namespace Hook.IntegrationTests.Matching;

[Collection("Pipeline-4")]
public sealed class ProviderQueryStatsTests : PipelineTestBase
{
    public ProviderQueryStatsTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task FindCandidates_PopulatesCompletedJobsAndSuccessRateFromProviderStats()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var query = scope.ServiceProvider.GetRequiredService<IProviderQueryService>();

        var slug = $"stats-test-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var location = new Location(DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);

        var withStats = ProviderAvailability.Register(
            $"+1415{Random.Shared.Next(1_000_000, 9_999_999)}",
            [slug], location, "SF", shareContact: true,
            ttl: TimeSpan.FromHours(1), now);
        var coldStart = ProviderAvailability.Register(
            $"+1415{Random.Shared.Next(1_000_000, 9_999_999)}",
            [slug], location, "SF", shareContact: true,
            ttl: TimeSpan.FromHours(1), now);

        var stats = ProviderStats.Initial(withStats.Phone, now);
        // i % 2 == 0 -> 5 completed jobs, 3 successes, SuccessRate = 0.6.
        // Decoupling CompletedCount from SuccessCount catches a column-symmetry bug
        // (e.g. the projection accidentally reading SuccessCount in both slots).
        for (var i = 0; i < 5; i++) stats.RecordOutcome(success: i % 2 == 0, now);

        // A third provider has a stats row but zero completed jobs — verifies the
        // zero-default path (CompletedCount=0, SuccessRate=0) is preserved.
        var zeroStats = ProviderAvailability.Register(
            $"+1415{Random.Shared.Next(1_000_000, 9_999_999)}",
            [slug], location, "SF", shareContact: true,
            ttl: TimeSpan.FromHours(1), now);
        var zero = ProviderStats.Initial(zeroStats.Phone, now);

        db.ProviderAvailabilities.AddRange(withStats, coldStart, zeroStats);
        db.ProviderStats.AddRange(stats, zero);
        await db.SaveChangesAsync();

        var requestPoint = location.ToPoint();
        var expanded = new ExpandedSlugs(slug, Parent: null, Children: Array.Empty<string>());
        var scored = await query.FindCandidatesAsync(
            requestPoint, expanded, radiusKm: 5, excludePhones: [], now);

        var hot = Assert.Single(scored, c => c.Candidate.Phone == withStats.Phone).Candidate;
        var cold = Assert.Single(scored, c => c.Candidate.Phone == coldStart.Phone).Candidate;
        var zeroCandidate = Assert.Single(scored, c => c.Candidate.Phone == zeroStats.Phone).Candidate;

        Assert.Equal(5, hot.CompletedJobs);
        Assert.Equal(0.6, hot.SuccessRate, precision: 6);
        Assert.Equal(0, cold.CompletedJobs);
        Assert.Equal(0, cold.SuccessRate);
        Assert.Equal(0, zeroCandidate.CompletedJobs);
        Assert.Equal(0, zeroCandidate.SuccessRate);
    }

    [Fact]
    public async Task ScoreAndRank_HotProviderOutranksColdStartAtEqualDistanceAndRecency()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var query = scope.ServiceProvider.GetRequiredService<IProviderQueryService>();
        var scorer = scope.ServiceProvider.GetRequiredService<MatchScorer>();

        var slug = $"rank-test-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        // Both providers registered 24h ago (recency < 1) so the cold-start
        // distance/recency renormalisation cannot tie the hot provider's
        // success-term advantage.
        var registeredAt = now - TimeSpan.FromHours(24);
        var location = new Location(DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);

        var hot = ProviderAvailability.Register(
            $"+1415{Random.Shared.Next(1_000_000, 9_999_999)}",
            [slug], location, "SF", shareContact: true,
            ttl: TimeSpan.FromHours(48), registeredAt);
        var cold = ProviderAvailability.Register(
            $"+1415{Random.Shared.Next(1_000_000, 9_999_999)}",
            [slug], location, "SF", shareContact: true,
            ttl: TimeSpan.FromHours(48), registeredAt);

        var stats = ProviderStats.Initial(hot.Phone, now);
        for (var i = 0; i < 5; i++) stats.RecordOutcome(success: true, now);

        db.ProviderAvailabilities.AddRange(hot, cold);
        db.ProviderStats.Add(stats);
        await db.SaveChangesAsync();

        var requestPoint = location.ToPoint();
        var expanded = new ExpandedSlugs(slug, Parent: null, Children: Array.Empty<string>());
        var scored = await query.FindCandidatesAsync(
            requestPoint, expanded, radiusKm: 5, excludePhones: [], now);

        var ranked = scorer.ScoreAndRank(scored, radiusKm: 5, now, take: 10);

        Assert.Equal(hot.Phone, ranked[0].Candidate.Phone);
        Assert.True(
            ranked[0].Score > ranked[1].Score,
            $"hot {ranked[0].Score} should beat cold {ranked[1].Score}");
    }
}
