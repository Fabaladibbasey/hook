using NetTopologySuite.Geometries;

namespace Hook.Features.Matching.Match;

public interface IProviderQueryService
{
    Task<IReadOnlyList<ProviderCandidate>> FindCandidatesAsync(
        Point requestLocation,
        string serviceSlug,
        double radiusKm,
        IEnumerable<string> excludePhones,
        DateTimeOffset now,
        CancellationToken ct = default);
}
