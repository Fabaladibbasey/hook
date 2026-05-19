using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;

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
        // Per-row TryInsertUniqueAsync absorbs the concurrent-boot 23505 race.
        var added = 0;
        foreach (var slug in RootSlugs)
        {
            var svc = Service.Create(slug);
            if (await db.TryInsertUniqueAsync(svc, ct, ServicesPrimaryKey)) added++;
        }

        if (added > 0)
            logger.LogInformation("[Taxonomy] Inserted {Count} root sectors.", added);
    }
}
