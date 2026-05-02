namespace Hook.Features.Matching.Match;

public sealed record ProviderCandidate(
    string Phone,
    bool ShareContact,
    DateTimeOffset LastActiveAt,
    double DistanceKm,
    int CompletedJobs,
    double SuccessRate);
