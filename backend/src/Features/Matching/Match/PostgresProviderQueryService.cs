using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Hook.Features.Matching.Match;

public sealed class PostgresProviderQueryService(HookDbContext db) : IProviderQueryService
{
    public async Task<IReadOnlyList<ScoredProviderCandidate>> FindCandidatesAsync(
        Point requestLocation,
        ExpandedSlugs slugs,
        double radiusKm,
        IEnumerable<string> excludePhones,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var radiusMeters = radiusKm * 1000.0;
        var excludeSet = excludePhones.ToHashSet();
        var allSlugs = slugs.All;

        var rows = await db.ProviderAvailabilities
            .Where(p => p.ExpiresAt > now)
            .Where(p => allSlugs.Any(slug => p.Services.Contains(slug)))
            .Where(p => !excludeSet.Contains(p.Phone))
            .Where(p => p.Location.IsWithinDistance(requestLocation, radiusMeters))
            .Select(p => new
            {
                p.Phone,
                p.ShareContact,
                p.LastActiveAt,
                p.Services,
                DistanceMeters = p.Location.Distance(requestLocation),
            })
            .ToListAsync(ct);

        // Split the per-row correlated stats subquery out into one hash-join
        // round-trip. Hierarchy expansion widens the candidate pool ~Nx, so the
        // old inline FirstOrDefault scaled with the result set instead of being
        // constant.
        var phones = rows.Select(r => r.Phone).ToArray();
        var stats = phones.Length == 0
            ? new Dictionary<string, (int CompletedCount, double SuccessRate)>(StringComparer.Ordinal)
            : await db.ProviderStats
                .Where(s => phones.Contains(s.ProviderPhone))
                .Select(s => new { s.ProviderPhone, s.CompletedCount, s.SuccessRate })
                .ToDictionaryAsync(s => s.ProviderPhone, s => (s.CompletedCount, s.SuccessRate),
                    StringComparer.Ordinal, ct);

        return [.. rows.Select(r =>
        {
            var stat = stats.TryGetValue(r.Phone, out var s) ? s : default;
            return new ScoredProviderCandidate(
                new ProviderCandidate(
                    r.Phone,
                    r.ShareContact,
                    r.LastActiveAt,
                    r.DistanceMeters / 1000.0,
                    CompletedJobs: stat.CompletedCount,
                    SuccessRate: stat.SuccessRate),
                slugs.Classify(r.Services));
        })];
    }
}
