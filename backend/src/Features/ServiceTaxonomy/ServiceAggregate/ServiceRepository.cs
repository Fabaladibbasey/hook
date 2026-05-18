using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.ServiceTaxonomy.ServiceAggregate;

public sealed class ServiceRepository(HookDbContext db) : IServiceRepository
{
    public Task<Service?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        db.Services.FirstOrDefaultAsync(s => s.Slug == slug, ct);

    public async Task<IReadOnlyList<SlugSimilarity>> FindSimilarAsync(string slug, int take, CancellationToken ct = default)
    {
        var rows = await db.Services
            .Select(s => new { s.Slug, Similarity = EF.Functions.TrigramsSimilarity(s.Slug, slug) })
            .Where(x => x.Similarity > 0)
            .OrderByDescending(x => x.Similarity)
            .Take(take)
            .ToListAsync(ct);

        return [.. rows.Select(x => new SlugSimilarity(x.Slug, x.Similarity))];
    }

    public async Task AddAsync(Service service, CancellationToken ct = default) =>
        await db.Services.AddAsync(service, ct);
}
