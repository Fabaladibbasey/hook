using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.ServiceTaxonomy.ServiceAggregate;

public sealed class ServiceRepository(HookDbContext db) : IServiceRepository
{
    public Task<Service?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        db.Services.FirstOrDefaultAsync(s => s.Slug == slug, ct);

    public async Task<IReadOnlyList<SlugSimilarity>> FindSimilarAsync(
        string slug,
        int take,
        CancellationToken ct = default)
    {
        // `%` predicate engages ix_services_slug_trgm. Bounded by the
        // session-level pg_trgm.similarity_threshold (default 0.3) — well below
        // ServiceTaxonomyOptions.AiJudgeThreshold (0.5), so no resolver
        // behaviour change.
        var rows = await db.Services
            .Where(s => EF.Functions.TrigramsAreSimilar(s.Slug, slug))
            .Select(s => new { s.Slug, Similarity = EF.Functions.TrigramsSimilarity(s.Slug, slug) })
            .Where(x => x.Similarity > 0)
            .OrderByDescending(x => x.Similarity)
            .Take(take)
            .ToListAsync(ct);

        return [.. rows.Select(x => new SlugSimilarity(x.Slug, x.Similarity))];
    }

    public async Task AddAsync(Service service, CancellationToken ct = default) =>
        await db.Services.AddAsync(service, ct);

    public async Task<ExpandedSlugs> ExpandAsync(string slug, CancellationToken ct = default)
    {
        // Single round-trip: rows are either the slug itself OR its direct children.
        // Partition in-memory rather than issue self+children as separate queries.
        var rows = await db.Services
            .Where(s => s.Slug == slug || s.ParentSlug == slug)
            .Select(s => new { s.Slug, s.ParentSlug })
            .ToListAsync(ct);

        string? parent = null;
        var children = new List<string>();
        var found = false;
        foreach (var row in rows)
        {
            if (row.Slug == slug)
            {
                parent = row.ParentSlug;
                found = true;
            }
            else
            {
                children.Add(row.Slug);
            }
        }

        // Unknown slug: return Requested only — query will hit zero children and
        // no parent. Matching just sees the literal slug; no broaden/narrow happens.
        return found
            ? new ExpandedSlugs(slug, parent, children)
            : new ExpandedSlugs(slug, Parent: null, Children: Array.Empty<string>());
    }
}
