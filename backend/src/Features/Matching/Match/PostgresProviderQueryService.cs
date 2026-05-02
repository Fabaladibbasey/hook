using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Hook.Features.Matching.Match;

public sealed class PostgresProviderQueryService(HookDbContext db) : IProviderQueryService
{
    public async Task<IReadOnlyList<ProviderCandidate>> FindCandidatesAsync(
        Point requestLocation,
        string serviceSlug,
        double radiusKm,
        IEnumerable<string> excludePhones,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var radiusMeters = radiusKm * 1000.0;
        var excludeSet = excludePhones.ToHashSet();

        var rows = await db.ProviderAvailabilities
            .Where(p => p.ExpiresAt > now)
            .Where(p => p.Services.Contains(serviceSlug))
            .Where(p => !excludeSet.Contains(p.Phone))
            .Where(p => p.Location.IsWithinDistance(requestLocation, radiusMeters))
            .Select(p => new
            {
                p.Phone,
                p.ShareContact,
                p.LastActiveAt,
                DistanceMeters = p.Location.Distance(requestLocation)
            })
            .ToListAsync(ct);

        return rows.Select(r => new ProviderCandidate(
            r.Phone,
            r.ShareContact,
            r.LastActiveAt,
            r.DistanceMeters / 1000.0,
            CompletedJobs: 0,
            SuccessRate: 0)).ToList();
    }
}
