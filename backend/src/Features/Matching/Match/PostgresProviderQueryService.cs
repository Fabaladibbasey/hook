using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using ProviderAvailabilityEntity = Hook.Features.ProviderAvailability.AvailabilityAggregate.ProviderAvailability;

namespace Hook.Features.Matching.Match;

public sealed class PostgresProviderQueryService(
    HookDbContext db,
    IOptions<MatchingOptions> options) : IProviderQueryService
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
        var excludeArray = excludePhones as string[] ?? excludePhones.ToArray();
        var opts = options.Value;

        // Per-slug `@>` branches union into a bitmap-OR scan over
        // ix_provider_availabilities_services_gin (jsonb_path_ops). A single
        // `allSlugs.Any(...)` predicate flattens to `?|` / EXISTS-unnest which
        // jsonb_path_ops does NOT support — Postgres falls back to seq scan.
        IQueryable<ProviderAvailabilityEntity>? combined = null;
        foreach (var slug in slugs.All)
        {
            var needle = $"[\"{slug}\"]";
            var branch = db.ProviderAvailabilities
                .Where(p => p.ExpiresAt > now)
                .Where(p => EF.Functions.JsonContains(p.Services, needle))
                .Where(p => !excludeArray.Contains(p.Phone))
                .Where(p => p.Location.IsWithinDistance(requestLocation, radiusMeters));
            combined = combined is null ? branch : combined.Union(branch);
        }

        if (combined is null) return [];

        var rows = await combined
            .OrderBy(p => p.Location.Distance(requestLocation))
            .Take(opts.MaxCandidatePoolSize)
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
