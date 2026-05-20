using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace Hook.Features.Matching.Match;

public sealed class PostgresProviderQueryService(
    HookDbContext db,
    IDbContextFactory<HookDbContext> dbFactory,
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
        if (slugs.All.Count == 0) return [];

        var radiusMeters = radiusKm * 1000.0;
        var excludeArray = excludePhones as string[] ?? excludePhones.ToArray();
        var opts = options.Value;
        var perBranchLimit = opts.MaxCandidatePoolSize;

        // Per-slug branches each push `ORDER BY <-> + LIMIT K` into the plan so the
        // GiST KNN scan trims to K rows BEFORE transport, then merge top-K
        // client-side. The previous Union'd query materialised the full union in
        // sort memory before applying the outer Take. Parallel branches use the
        // factory ctx — the scoped HookDbContext is reserved for the Wolverine
        // handler tx and cannot multiplex.
        var branchTasks = slugs.All.Select(slug => RunBranchAsync(
            slug, requestLocation, radiusMeters, excludeArray, now, perBranchLimit, ct));

        var branchResults = await Task.WhenAll(branchTasks);

        var rows = branchResults
            .SelectMany(b => b)
            .GroupBy(r => r.Phone, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(r => r.DistanceMeters)
            .Take(opts.MaxCandidatePoolSize)
            .ToList();

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

    private async Task<List<BranchRow>> RunBranchAsync(
        string slug,
        Point requestLocation,
        double radiusMeters,
        string[] excludeArray,
        DateTimeOffset now,
        int perBranchLimit,
        CancellationToken ct)
    {
        var needle = $"[\"{slug}\"]";
        await using var ctx = await dbFactory.CreateDbContextAsync(ct);
        return await ctx.ProviderAvailabilities
            .AsNoTracking()
            .Where(p => p.ExpiresAt > now)
            .Where(p => EF.Functions.JsonContains(p.Services, needle))
            .Where(p => !excludeArray.Contains(p.Phone))
            .Where(p => p.Location.IsWithinDistance(requestLocation, radiusMeters))
            .OrderBy(p => p.Location.Distance(requestLocation))
            .Take(perBranchLimit)
            .Select(p => new BranchRow
            {
                Phone = p.Phone,
                ShareContact = p.ShareContact,
                LastActiveAt = p.LastActiveAt,
                Services = p.Services,
                DistanceMeters = p.Location.Distance(requestLocation),
            })
            .ToListAsync(ct);
    }

    private sealed class BranchRow
    {
        public required string Phone { get; init; }
        public bool ShareContact { get; init; }
        public DateTimeOffset LastActiveAt { get; init; }
        public required List<string> Services { get; init; }
        public double DistanceMeters { get; init; }
    }
}
