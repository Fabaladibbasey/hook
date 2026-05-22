using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

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
        foreach (var slug in missing)
            await db.Services.AddAsync(Service.Create(slug), ct);

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("[Taxonomy] Inserted {Count} root sectors.", missing.Count);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg
            && pg.ConstraintName == ServicesPrimaryKey)
        {
            foreach (var entry in db.ChangeTracker.Entries<Service>().ToList())
                if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
            logger.LogDebug(ex, "Concurrent boot raced root seed; ignoring.");
        }
    }
}
