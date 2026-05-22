using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hook.Features.ServiceTaxonomy.SeedRoots;

public sealed class RootSectorSeeder(
    HookDbContext db,
    ILogger<RootSectorSeeder> logger)
{
    private const string ServicesPrimaryKey = "PK_services";

    // Append-only — removing a root SET NULLs its children's ParentSlug.
    public static readonly IReadOnlyList<string> RootSlugs =
    [
        "doctor",
        "software-engineering",
        "lawyer",
        "mechanic",
        "electrician",
        "plumbing",
        "teacher",
        "beautician",
        "carpenter",
        "tailor",
        "driver",
        "photographer",
        "cleaner",
        "barber",
        "accountant",
        "tutor",
    ];

    public static readonly IReadOnlySet<string> RootSlugSet =
        new HashSet<string>(RootSlugs, StringComparer.Ordinal);

    public async Task EnsureRootSectorsAsync(CancellationToken ct = default)
    {
        var existing = await db.Services
            .Where(s => RootSlugs.Contains(s.Slug))
            .Select(s => s.Slug)
            .ToListAsync(ct);

        if (existing.Count == RootSlugs.Count) return;

        var missing = RootSlugs.Except(existing).ToList();

        // Cold-boot fast path: 16 sequential TryInsertUniqueAsync calls cost
        // 50-80ms of managed-DB RTT. When ALL roots are missing, a single
        // AddRange + SaveChanges is one round-trip. The seeder runs outside any
        // Wolverine handler tx so there's no enclosing transaction to corrupt;
        // a unique-race on commit detaches and falls back to per-slug retry.
        if (existing.Count == 0)
        {
            var entities = missing.Select(s => Service.Create(s)).ToArray();
            await db.Services.AddRangeAsync(entities, ct);
            try
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation("[Taxonomy] Inserted {Count} root sectors.", entities.Length);
                return;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // A peer host raced us between our existence query and our insert.
                // Detach the failed batch so the per-slug fallback below starts clean.
                foreach (var entity in entities)
                    db.Entry(entity).State = EntityState.Detached;
            }
        }

        var inserted = 0;
        foreach (var slug in missing)
        {
            if (await db.TryInsertUniqueAsync(Service.Create(slug), ct, ServicesPrimaryKey))
                inserted++;
        }

        if (inserted > 0)
            logger.LogInformation("[Taxonomy] Inserted {Count} root sectors.", inserted);
    }
}
