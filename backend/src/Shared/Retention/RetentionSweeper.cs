using System.Collections.ObjectModel;
using System.Diagnostics;
using Hook.Features.Feedback.Models;
using Hook.Features.Observability;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hook.Shared.Retention;

public sealed class RetentionSweeper(
    HookDbContext db,
    IOptions<RetentionOptions> options,
    TimeProvider clock,
    ILogger<RetentionSweeper> logger) : IRetentionSweeper
{
    private const long AdvisoryLockKey = 8675309L;

    public async Task<RetentionSweepResult> RunOnceAsync(CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Retention sweep skipped: disabled");
            return new RetentionSweepResult(
                new ReadOnlyDictionary<string, int>(new Dictionary<string, int>()));
        }

        var nowUtc = clock.GetUtcNow();
        var cutoff = nowUtc - TimeSpan.FromDays(opts.RetentionDays);
        var dlqCutoff = nowUtc - TimeSpan.FromDays(opts.DeadLetterRetentionDays);
        var pendingCutoff = nowUtc - opts.PendingFeedbackClaimAfter;
        var platformAnswerDedupCutoff = nowUtc - opts.PlatformAnswerDedupCleanupAfter;

        var sweeps = new (string Key, string Op, Func<Task<int>> Run)[]
        {
            (RetentionTableKeys.ChatSessions, RetentionOps.Delete,
                () => db.ChatSessions.Where(s => s.ExpiresAt < cutoff).ExecuteDeleteAsync(ct)),
            (RetentionTableKeys.ServiceRequests, RetentionOps.Delete,
                () => db.ServiceRequests
                    .Where(r => r.CreatedAt < cutoff && r.Status == ServiceRequestStatus.Closed)
                    .ExecuteDeleteAsync(ct)),
            (RetentionTableKeys.ProviderAvailabilities, RetentionOps.Delete,
                () => db.ProviderAvailabilities.Where(p => p.ExpiresAt < cutoff).ExecuteDeleteAsync(ct)),
            (RetentionTableKeys.WhatsappContacts, RetentionOps.Delete,
                () => db.WhatsappContacts.Where(c => c.LastInboundAt < cutoff).ExecuteDeleteAsync(ct)),
            (RetentionTableKeys.GeocodeCache, RetentionOps.Delete,
                () => db.GeocodeCache.Where(g => g.FetchedAt < cutoff).ExecuteDeleteAsync(ct)),
            (RetentionTableKeys.ProviderRegistrationDrafts, RetentionOps.Delete,
                () => db.RegistrationDrafts.Where(d => d.UpdatedAt < cutoff).ExecuteDeleteAsync(ct)),
            (RetentionTableKeys.ClientRequestDrafts, RetentionOps.Delete,
                () => db.ClientRequestDrafts.Where(d => d.UpdatedAt < cutoff).ExecuteDeleteAsync(ct)),
            (RetentionTableKeys.AmbiguousIntentDrafts, RetentionOps.Delete,
                () => db.AmbiguousIntentDrafts.Where(d => d.CreatedAt < cutoff).ExecuteDeleteAsync(ct)),
            // Claim stale Pending rows BEFORE the delete sweep below so a row that
            // crosses both thresholds gets the UPDATE (Pending → Skipped) before the
            // 7-day DELETE. Bulk SetProperty bypasses the domain ClaimSkipped method;
            // safe because (a) ClaimSkipped only sets Answer/RepliedAt/Version — the
            // three columns we set; (b) tracked-aggregate update would not scale on
            // large backlogs. Version+1 keeps optimistic concurrency: any racing
            // in-memory aggregate loses its SaveChanges.
            (RetentionTableKeys.MatchFeedbackPendingClaimed, RetentionOps.Update,
                () => db.MatchFeedback
                    .Where(f => f.Answer == FeedbackAnswer.Pending && f.PromptedAt < pendingCutoff)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(f => f.Answer, FeedbackAnswer.Skipped)
                        .SetProperty(f => f.RepliedAt, nowUtc)
                        .SetProperty(f => f.Version, f => f.Version + 1), ct)),
            (RetentionTableKeys.MatchFeedback, RetentionOps.Delete,
                () => db.MatchFeedback.Where(f => f.PromptedAt < cutoff).ExecuteDeleteAsync(ct)),
            // Tiny per-(phone, question-hash) dedup rows have no signal past the
            // configured DedupWindowSeconds — prune at PlatformAnswerDedupCleanupAfter
            // (default 1h) rather than the 7d global retention so the table stays
            // bounded under question-spam load.
            (RetentionTableKeys.PlatformAnswerDedup, RetentionOps.Delete,
                () => db.PlatformAnswerDedup.Where(d => d.AnsweredAt < platformAnswerDedupCutoff).ExecuteDeleteAsync(ct)),
            // Wolverine DLQ rows persist indefinitely after MaxAttempts failure;
            // their JSON body may include user text + unmasked phone for AI-stage envelopes.
            // Schema is a code const, not user input — must be concatenated, not parameterized,
            // since Postgres rejects parameters in identifier position (42601).
            // NOTE: Wolverine's `received_at` is the listener address URI (varchar) — not a
            // timestamp. `sent_at` (timestamptz) is the actual envelope send time and the
            // right axis for retention.
            (RetentionTableKeys.WolverineDeadLetters, RetentionOps.Delete,
                () => db.Database.ExecuteSqlRawAsync(
                    $"DELETE FROM {WolverineConfig.Schema}.{RetentionTableKeys.WolverineDeadLetters} "
                        + "WHERE sent_at < {0}",
                    [dlqCutoff], ct)),
        };

        var counts = new Dictionary<string, int>(sweeps.Length);
        var sw = Stopwatch.StartNew();

        var lockAcquired = false;
        try
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_lock({0});", [AdvisoryLockKey], ct);
                lockAcquired = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not acquire retention advisory lock; proceeding without it");
            }

            foreach (var (key, op, run) in sweeps)
            {
                try
                {
                    counts[key] = await run();
                    HookMetrics.RetentionSwept.Add(
                        counts[key],
                        new KeyValuePair<string, object?>("table", key),
                        new KeyValuePair<string, object?>("op", op));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Retention sweep failed for {Table}; skipping", key);
                    counts[key] = -1;
                    HookMetrics.RetentionSweepErrors.Add(
                        1,
                        new KeyValuePair<string, object?>("table", key),
                        new KeyValuePair<string, object?>("op", op));
                }
            }
        }
        finally
        {
            if (lockAcquired)
            {
                try
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "SELECT pg_advisory_unlock({0});", [AdvisoryLockKey], CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not release retention advisory lock");
                }
            }

            sw.Stop();
            HookMetrics.RetentionSweepDuration.Record(sw.Elapsed.TotalMilliseconds);
        }

        var total = counts.Values.Where(v => v >= 0).Sum();
        logger.LogInformation(
            "Retention sweep complete: cutoff={Cutoff} total_swept={Total} "
                + "per_table={@PerTable} duration_ms={DurationMs}",
            cutoff,
            total,
            counts,
            sw.Elapsed.TotalMilliseconds);

        return new RetentionSweepResult(new ReadOnlyDictionary<string, int>(counts));
    }
}
