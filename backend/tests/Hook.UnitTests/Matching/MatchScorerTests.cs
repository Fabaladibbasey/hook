using Hook.Features.Matching;
using Hook.Features.Matching.Match;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Hook.UnitTests.Matching;

public class MatchScorerTests
{
    private static MatchScorer Build(MatchingOptions opts) => new(Options.Create(opts));

    [Fact]
    public void Score_ShouldFavorCloserAndMoreRecentProvider()
    {
        var opts = new MatchingOptions();
        var scorer = Build(opts);
        var now = DateTimeOffset.Parse("2026-05-01T12:00:00Z");
        var radius = 10.0;

        var close = new ProviderCandidate("+1", true, now.AddMinutes(-5), DistanceKm: 1, CompletedJobs: 0, SuccessRate: 0);
        var far = new ProviderCandidate("+2", true, now.AddHours(-30), DistanceKm: 9, CompletedJobs: 0, SuccessRate: 0);

        var ranked = scorer.ScoreAndRank(new[] { far, close }, radius, now, take: 2);

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
        var scored = scorer.Score(fresh, radiusKm: 5, now);

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
        var scored = scorer.Score(experienced, radiusKm: 5, now);

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

        var s1 = scorer.Score(atEdge, radiusKm: 5, now);
        var s2 = scorer.Score(beyond, radiusKm: 5, now);

        s1.Score.ShouldBeGreaterThanOrEqualTo(0);
        s2.Score.ShouldBeGreaterThanOrEqualTo(0);
        s1.Score.ShouldBeGreaterThanOrEqualTo(s2.Score);
    }
}
