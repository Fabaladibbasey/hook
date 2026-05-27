using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Hook.Shared.Persistence;

// Single round-trip "INSERT new row OR refresh-on-conflict only when stamp has
// aged past the window" — RETURNING emits exactly one row on both INSERT and
// the WHERE-satisfied refresh, nothing when the WHERE-stale predicate fails.
// Postgres-specific (ON CONFLICT). The caller owns the SQL string; this helper
// centralises the execution shape so callers cannot drift on the materialisation
// idiom (AsAsyncEnumerable.AnyAsync vs ToListAsync.Count).
//
// Why AsAsyncEnumerable instead of AnyAsync directly: EF's compose-over-raw-SQL
// guard rejects AnyAsync on a non-composable INSERT…RETURNING source. Streaming
// the at-most-one-row result via AnyAsync is equivalent to ToListAsync().Count
// without the throwaway List allocation.
public static class TimedDedupGate
{
    public static async Task<bool> TryClaimAsync(
        DatabaseFacade db,
        string insertOnConflictReturning1Sql,
        object[] parameters,
        CancellationToken ct) =>
        await db.SqlQueryRaw<int>(insertOnConflictReturning1Sql, parameters)
            .AsAsyncEnumerable()
            .AnyAsync(ct);
}
