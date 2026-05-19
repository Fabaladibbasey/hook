using Hook.Features.Matching;
using Hook.Features.Matching.Match;
using Hook.Features.Matching.MatchAggregate;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Hook.UnitTests.Matching;

public class MatchScorerTests
{
    private static MatchScorer Build(MatchingOptions opts) => new(Options.Create(opts));

    private static ScoredProviderCandidate Exact(ProviderCandidate c) => new(c, MatchKind.Exact);

    [Fact]
    public void Score_ShouldFavorCloserAndMoreRecentProvider()
    {
        var opts = new MatchingOptions();
        var scorer = Build(opts);
        var now = DateTimeOffset.Parse("2026-05-01T12:00:00Z");
        var radius = 10.0;

        var close = new ProviderCandidate("+1", true, now.AddMinutes(-5), DistanceKm: 1, CompletedJobs: 0, SuccessRate: 0);
        var far = new ProviderCandidate("+2", true, now.AddHours(-30), DistanceKm: 9, CompletedJobs: 0, SuccessRate: 0);

        var ranked = scorer.ScoreAndRank([Exact(far), Exact(close)], radius, now, take: 2);

        ranked[0].Candidate.Phone.ShouldBe("+1");
        ranked[1].Candidate.Phone.ShouldBe("+2");
    }

    [Fact]
    public void Score_ShouldUseColdStartReweighting_BelowMinJobs()
    {
        var opts = new MatchingOptions { ColdStartMinJobs = 3 };
        var scorer = Build(opts);
        var now = DateTimeOffset.UtcNow;

        var fresh = new ProviderCandidate("+1", true, now, DistanceKm: 0, CompletedJobs: 0, SuccessRate: 0);
        var scored = scorer.Score(Exact(fresh), radiusKm: 5, now);

        var dwSum = opts.DistanceWeight + opts.RecencyWeight;
        var dw = opts.DistanceWeight / dwSum;
        var rw = opts.RecencyWeight / dwSum;
        var expected = (dw * 1.0) + (rw * 1.0);

        scored.Score.ShouldBe(expected, 0.0001);
    }

    [Fact]
    public void Score_ShouldIncludeSuccessTerm_WhenAtOrAboveMinJobs()
    {
        var opts = new MatchingOptions { ColdStartMinJobs = 3 };
        var scorer = Build(opts);
        var now = DateTimeOffset.UtcNow;

        var experienced = new ProviderCandidate("+1", true, now, DistanceKm: 0, CompletedJobs: 5, SuccessRate: 1);
        var scored = scorer.Score(Exact(experienced), radiusKm: 5, now);

        var expected = (opts.DistanceWeight * 1.0) + (opts.RecencyWeight * 1.0) + (opts.SuccessWeight * 1.0);
        scored.Score.ShouldBe(expected, 0.0001);
    }

    [Fact]
    public void Score_ShouldClampProximityAtRadiusBoundary()
    {
        var scorer = Build(new MatchingOptions());
        var now = DateTimeOffset.UtcNow;

        var atEdge = new ProviderCandidate("+1", true, now.AddYears(-10), DistanceKm: 5, CompletedJobs: 0, SuccessRate: 0);
        var beyond = new ProviderCandidate("+2", true, now.AddYears(-10), DistanceKm: 99, CompletedJobs: 0, SuccessRate: 0);

        var s1 = scorer.Score(Exact(atEdge), radiusKm: 5, now);
        var s2 = scorer.Score(Exact(beyond), radiusKm: 5, now);

        s1.Score.ShouldBeGreaterThanOrEqualTo(0);
        s2.Score.ShouldBeGreaterThanOrEqualTo(0);
        s1.Score.ShouldBeGreaterThanOrEqualTo(s2.Score);
    }

    [Fact]
    public void Score_AppliesBroadenedFactor_OnBroadenedKind()
    {
        var opts = new MatchingOptions { BroadenedMatchFactor = 0.4, NarrowedMatchFactor = 0.6, ColdStartMinJobs = 0 };
        var scorer = Build(opts);
        var now = DateTimeOffset.UtcNow;

        var c = new ProviderCandidate("+1", true, now, DistanceKm: 0, CompletedJobs: 5, SuccessRate: 1);
        var exact = scorer.Score(new ScoredProviderCandidate(c, MatchKind.Exact), radiusKm: 5, now);
        var broadened = scorer.Score(new ScoredProviderCandidate(c, MatchKind.Broadened), radiusKm: 5, now);

        broadened.Kind.ShouldBe(MatchKind.Broadened);
        broadened.Score.ShouldBe(exact.Score * 0.4, 0.0001);
    }

    [Fact]
    public void Score_AppliesNarrowedFactor_OnNarrowedKind()
    {
        var opts = new MatchingOptions { BroadenedMatchFactor = 0.4, NarrowedMatchFactor = 0.6, ColdStartMinJobs = 0 };
        var scorer = Build(opts);
        var now = DateTimeOffset.UtcNow;

        var c = new ProviderCandidate("+1", true, now, DistanceKm: 0, CompletedJobs: 5, SuccessRate: 1);
        var exact = scorer.Score(new ScoredProviderCandidate(c, MatchKind.Exact), radiusKm: 5, now);
        var narrowed = scorer.Score(new ScoredProviderCandidate(c, MatchKind.Narrowed), radiusKm: 5, now);

        narrowed.Kind.ShouldBe(MatchKind.Narrowed);
        narrowed.Score.ShouldBe(exact.Score * 0.6, 0.0001);
    }

    [Fact]
    public void DefaultOptions_PenaliseBroadenedHarderThanNarrowed()
    {
        // Broadened is the abuse-prone direction (provider claims to be the
        // generalist parent and rides every child request), so the default
        // factor must penalise it more than narrowed (legitimate specialist).
        var opts = new MatchingOptions();
        opts.BroadenedMatchFactor.ShouldBeLessThan(opts.NarrowedMatchFactor);
    }

    [Fact]
    public void Options_BindFromConfiguration_ForBroadenedAndNarrowedFactors()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Matching:BroadenedMatchFactor"] = "0.35",
                ["Matching:NarrowedMatchFactor"] = "0.75",
            })
            .Build();

        var opts = config.GetSection(MatchingOptions.SectionName).Get<MatchingOptions>()!;

        opts.BroadenedMatchFactor.ShouldBe(0.35);
        opts.NarrowedMatchFactor.ShouldBe(0.75);
    }

    [Fact]
    public void ScoreAndRank_FactorFlipsOrder_WhenBroadenedHasHigherBase()
    {
        // Broadened candidate has a strictly stronger base (closer + more
        // recent), but the 0.4 factor downgrades it below the Exact match.
        // Guards against a regression where factor is multiplied AFTER the
        // OrderBy pass.
        var opts = new MatchingOptions { BroadenedMatchFactor = 0.4, NarrowedMatchFactor = 0.6, ColdStartMinJobs = 0 };
        var scorer = Build(opts);
        var now = DateTimeOffset.UtcNow;

        var strongBroadened = new ProviderCandidate("+broad", true, now, DistanceKm: 0, CompletedJobs: 10, SuccessRate: 1);
        var weakerExact = new ProviderCandidate("+exact", true, now.AddHours(-6), DistanceKm: 3, CompletedJobs: 5, SuccessRate: 0.5);

        var ranked = scorer.ScoreAndRank(
            [
                new ScoredProviderCandidate(strongBroadened, MatchKind.Broadened),
                new ScoredProviderCandidate(weakerExact, MatchKind.Exact),
            ],
            radiusKm: 5, now, take: 2);

        ranked[0].Candidate.Phone.ShouldBe("+exact");
        ranked[0].Kind.ShouldBe(MatchKind.Exact);
        ranked[1].Kind.ShouldBe(MatchKind.Broadened);
    }

    [Fact]
    public void Score_ExactBeatsCrossLevel_WhenAllElseEqual()
    {
        var opts = new MatchingOptions { BroadenedMatchFactor = 0.5, NarrowedMatchFactor = 0.5, ColdStartMinJobs = 0 };
        var scorer = Build(opts);
        var now = DateTimeOffset.UtcNow;

        var c = new ProviderCandidate("+1", true, now, DistanceKm: 1, CompletedJobs: 5, SuccessRate: 1);
        var ranked = scorer.ScoreAndRank(
            [
                new ScoredProviderCandidate(c, MatchKind.Broadened),
                new ScoredProviderCandidate(c with { Phone = "+2" }, MatchKind.Exact),
            ],
            radiusKm: 5, now, take: 2);

        ranked[0].Candidate.Phone.ShouldBe("+2");
        ranked[0].Kind.ShouldBe(MatchKind.Exact);
    }
}
