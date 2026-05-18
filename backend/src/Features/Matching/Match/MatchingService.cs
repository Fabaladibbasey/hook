using Hook.Features.Observability;
using Hook.Features.ServiceRequest.RequestAggregate;
using Microsoft.Extensions.Options;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;

namespace Hook.Features.Matching.Match;

public sealed record MatchBatch(
    Guid RequestId,
    IReadOnlyList<MatchEntity> NewMatches,
    IReadOnlyList<ScoredCandidate> Scored);

public sealed class MatchingService(
    IServiceRequestRepository requests,
    IProviderQueryService query,
    IMatchRepository matches,
    MatchScorer scorer,
    IOptions<MatchingOptions> options,
    TimeProvider clock)
{
    // Caller contract: invoke only inside a Wolverine handler — repository
    // mutations are committed by AutoApplyTransactions at handler-end. Calling
    // outside a handler context silently loses writes; the codebase has a single
    // production caller (ServiceRequestCreatedHandler) so we don't pay the
    // test-infra cost of a runtime guard for a one-handler invariant.
    public async Task<MatchBatch?> RunForRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        var request = await requests.GetAsync(requestId, ct);
        if (request is null) return null;

        var now = clock.GetUtcNow();

        var radius = request.CurrentRadiusKm > 0 ? request.CurrentRadiusKm : options.Value.DefaultRadiusKm;

        // Exclude the client's own phone — a sender who is also a listed provider
        // at the same coords would otherwise self-match.
        var excludePhones = request.ShownProviderPhones.Append(request.ClientPhone);

        var candidates = await query.FindCandidatesAsync(
            request.Location,
            request.ServiceSlug,
            radius,
            excludePhones,
            now,
            ct);

        HookMetrics.MatchesPoolSize.Record(candidates.Count,
            new KeyValuePair<string, object?>("service", request.ServiceSlug));

        var scored = scorer.ScoreAndRank(candidates, radius, now, options.Value.TopMatchesPerBatch);

        HookMetrics.MatchesTotal.Add(scored.Count,
            new KeyValuePair<string, object?>("service", request.ServiceSlug));

        var newMatches = scored.Select(s => new MatchEntity
        {
            Id = Guid.NewGuid(),
            RequestId = request.Id,
            ProviderPhone = s.Candidate.Phone,
            ServiceSlug = request.ServiceSlug,
            DistanceKm = s.Candidate.DistanceKm,
            Score = s.Score,
            CreatedAt = now
        }).ToList();

        await matches.AddRangeAsync(newMatches, ct);

        request.RecordShown(scored.Select(s => s.Candidate.Phone));
        if (scored.Count > 0) request.MarkMatched();

        return new MatchBatch(request.Id, newMatches, scored);
    }
}
