using Hook.Features.Matching.MatchAggregate;

namespace Hook.Features.Matching.Match;

public sealed record ScoredProviderCandidate(ProviderCandidate Candidate, MatchKind Kind);
