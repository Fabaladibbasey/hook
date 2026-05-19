using Hook.Features.Matching.MatchAggregate;
using Microsoft.Extensions.Options;

namespace Hook.Features.Matching.Match;

public sealed record ScoredCandidate(ProviderCandidate Candidate, double Score, MatchKind Kind);

public sealed class MatchScorer(IOptions<MatchingOptions> options)
{
    public ScoredCandidate Score(ScoredProviderCandidate input, double radiusKm, DateTimeOffset now)
    {
        var opts = options.Value;
        var c = input.Candidate;

        var proximity = Math.Clamp(1.0 - (c.DistanceKm / radiusKm), 0.0, 1.0);
        var hoursSinceActive = (now - c.LastActiveAt).TotalHours;
        var recency = Math.Exp(-hoursSinceActive / opts.RecencyHalfLifeHours);

        double baseScore;
        if (c.CompletedJobs >= opts.ColdStartMinJobs)
        {
            baseScore = (opts.DistanceWeight * proximity)
                      + (opts.RecencyWeight * recency)
                      + (opts.SuccessWeight * c.SuccessRate);
        }
        else
        {
            var sum = opts.DistanceWeight + opts.RecencyWeight;
            var dw = opts.DistanceWeight / sum;
            var rw = opts.RecencyWeight / sum;
            baseScore = (dw * proximity) + (rw * recency);
        }

        var factor = input.Kind switch
        {
            MatchKind.Exact => 1.0,
            MatchKind.Broadened => opts.BroadenedMatchFactor,
            MatchKind.Narrowed => opts.NarrowedMatchFactor,
            _ => 1.0,
        };
        var score = baseScore * factor;

        return new ScoredCandidate(c, score, input.Kind);
    }

    public IReadOnlyList<ScoredCandidate> ScoreAndRank(
        IEnumerable<ScoredProviderCandidate> inputs,
        double radiusKm,
        DateTimeOffset now,
        int take)
    {
        return [.. inputs
            .Select(i => Score(i, radiusKm, now))
            .OrderByDescending(s => s.Score)
            .Take(take)];
    }
}
