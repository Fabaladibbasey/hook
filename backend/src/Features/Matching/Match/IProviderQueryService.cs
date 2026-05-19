using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using NetTopologySuite.Geometries;

namespace Hook.Features.Matching.Match;

public interface IProviderQueryService
{
    Task<IReadOnlyList<ScoredProviderCandidate>> FindCandidatesAsync(
        Point requestLocation,
        ExpandedSlugs slugs,
        double radiusKm,
        IEnumerable<string> excludePhones,
        DateTimeOffset now,
        CancellationToken ct = default);
}
