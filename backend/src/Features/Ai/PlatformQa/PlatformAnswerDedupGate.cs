using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hook.Features.Ai.PlatformQa;

public interface IPlatformAnswerDedupGate
{
    Task<bool> TryClaimAsync(string phone, long questionHash, CancellationToken ct);
}

public sealed class PlatformAnswerDedupGate(
    HookDbContext db,
    IOptions<PlatformAnswerOptions> options,
    TimeProvider clock) : IPlatformAnswerDedupGate
{
    // Single round-trip: INSERT new row OR refresh-on-conflict only when answered_at
    // has aged past the configured window. RETURNING emits a row on both INSERT and
    // the WHERE-satisfied UPDATE path; an unsatisfied WHERE returns nothing — that
    // is the "we answered <window> ago, skip" hot path.
    public async Task<bool> TryClaimAsync(string phone, long questionHash, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var cutoff = now - TimeSpan.FromSeconds(options.Value.DedupWindowSeconds);
        const string sql = """
            INSERT INTO platform_answer_dedup ("Phone", "QuestionHash", "AnsweredAt") VALUES ({0}, {1}, {2})
            ON CONFLICT ("Phone", "QuestionHash") DO UPDATE
                SET "AnsweredAt" = EXCLUDED."AnsweredAt"
              WHERE platform_answer_dedup."AnsweredAt" <= {3}
            RETURNING 1
            """;
        var rows = await db.Database
            .SqlQueryRaw<int>(sql, phone, questionHash, now, cutoff)
            .ToListAsync(ct);
        return rows.Count > 0;
    }
}
