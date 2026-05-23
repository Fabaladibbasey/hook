using Hook.Features.Matching.MatchAggregate;

namespace Hook.Features.Matching.PresentMatches.Dispatch;

public sealed record PresentedMatch(
    string ProviderPhone,
    double DistanceKm,
    double Score,
    MatchKind Kind);
