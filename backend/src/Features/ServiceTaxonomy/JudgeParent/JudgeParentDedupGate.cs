using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.ServiceTaxonomy.JudgeParent;

public interface IJudgeParentDedupGate
{
    Task<bool> TryClaimAsync(string slug, CancellationToken ct);
}

public sealed class JudgeParentDedupGate(HookDbContext db, TimeProvider clock) : IJudgeParentDedupGate
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    // Single round-trip: INSERT new row OR refresh-on-conflict only when judged_at
    // has aged past the window. RETURNING emits a row on both INSERT and the WHERE-
    // satisfied UPDATE path; an unsatisfied WHERE leaves the row untouched and
    // returns nothing. FirstOrDefaultAsync(0) ⇒ claimed=false for the hot
    // "seen <5min ago" path.
    public async Task<bool> TryClaimAsync(string slug, CancellationToken ct)
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
        var rows = await db.Database
            .SqlQueryRaw<int>(sql, slug, now, cutoff)
            .ToListAsync(ct);
        return rows.Count > 0;
    }
}
