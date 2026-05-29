using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;

namespace Hook.Features.ServiceTaxonomy.JudgeParent;

public interface IJudgeParentDedupGate
{
    Task<bool> TryClaimAsync(string slug, CancellationToken ct);
}

public sealed class JudgeParentDedupGate(HookDbContext db, TimeProvider clock) : IJudgeParentDedupGate
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    // Single round-trip dedup gate; see TimedDedupGate for the materialisation
    // contract. RETURNING emits a row on both INSERT and WHERE-satisfied UPDATE.
    public Task<bool> TryClaimAsync(string slug, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var cutoff = now - Window;
        const string sql = """
            INSERT INTO judge_parent_dedup ("Slug", "JudgedAt") VALUES ({0}, {1})
            ON CONFLICT ON CONSTRAINT "PK_judge_parent_dedup" DO UPDATE
                SET "JudgedAt" = EXCLUDED."JudgedAt"
              WHERE judge_parent_dedup."JudgedAt" <= {2}
            RETURNING 1
            """;
        return TimedDedupGate.TryClaimAsync(db.Database, sql, [slug, now, cutoff], ct);
    }
}
