using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;

namespace Hook.Features.ServiceTaxonomy.SeedRoots;

public sealed class RootSectorSeeder(
    HookDbContext db,
    ILogger<RootSectorSeeder> logger)
{
    private const string ServicesPrimaryKey = "PK_services";

    // Append-only. Never remove — removing a root after children exist would
    // SET NULL them (graceful but semantically lossy). Order is irrelevant
    // (membership lookup), so it's safe to reorder.
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

    public async Task EnsureRootSectorsAsync(CancellationToken ct = default)
    {
        // Per-row TryInsertUniqueAsync absorbs the concurrent-boot race (rolling
        // deploy / scale-out) where two hosts race the same PK and one would
        // otherwise crash on 23505. Constant cost vs the existing batched
        // SaveChanges and idempotent on re-run.
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
