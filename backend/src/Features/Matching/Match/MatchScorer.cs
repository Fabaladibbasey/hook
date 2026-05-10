using Microsoft.Extensions.Options;

namespace Hook.Features.Matching.Match;

public sealed record ScoredCandidate(ProviderCandidate Candidate, double Score);

public sealed class MatchScorer(IOptions<MatchingOptions> options)
{
    public ScoredCandidate Score(ProviderCandidate candidate, double radiusKm, DateTimeOffset now)
    {
        var opts = options.Value;

        var proximity = Math.Clamp(1.0 - (candidate.DistanceKm / radiusKm), 0.0, 1.0);

        var hoursSinceActive = (now - candidate.LastActiveAt).TotalHours;
        var recency = Math.Exp(-hoursSinceActive / opts.RecencyHalfLifeHours);

        var useSuccess = candidate.CompletedJobs >= opts.ColdStartMinJobs;
        double score;
        if (useSuccess)
        {
            score = (opts.DistanceWeight * proximity) +
                    (opts.RecencyWeight * recency) +
                    (opts.SuccessWeight * candidate.SuccessRate);
        }
        else
        {
            var sum = opts.DistanceWeight + opts.RecencyWeight;
            var dw = opts.DistanceWeight / sum;
            var rw = opts.RecencyWeight / sum;
            score = (dw * proximity) + (rw * recency);
        }

        return new ScoredCandidate(candidate, score);
    }

    public IReadOnlyList<ScoredCandidate> ScoreAndRank(
        IEnumerable<ProviderCandidate> candidates,
        double radiusKm,
        DateTimeOffset now,
        int take)
    {
        return [.. candidates
            .Select(c => Score(c, radiusKm, now))
            .OrderByDescending(s => s.Score)
            .Take(take)];
    }
}
