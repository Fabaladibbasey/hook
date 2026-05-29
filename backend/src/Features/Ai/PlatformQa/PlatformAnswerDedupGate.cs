using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;
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
    // Single round-trip dedup gate; see TimedDedupGate for the materialisation
    // contract. RETURNING emits a row on both INSERT and WHERE-satisfied UPDATE.
    public Task<bool> TryClaimAsync(string phone, long questionHash, CancellationToken ct)
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
        return TimedDedupGate.TryClaimAsync(db.Database, sql, [phone, questionHash, now, cutoff], ct);
    }
}
