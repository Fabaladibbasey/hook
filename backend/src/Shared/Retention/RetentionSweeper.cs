using System.Collections.ObjectModel;
using System.Diagnostics;
using Hook.Features.Observability;
using Hook.Features.ServiceRequest.RequestAggregate;
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

        var sweeps = new (string Key, Func<Task<int>> Run)[]
        {
            (RetentionTableKeys.ChatSessions,
                () => db.ChatSessions.Where(s => s.ExpiresAt < cutoff).ExecuteDeleteAsync(ct)),
            (RetentionTableKeys.ServiceRequests,
                () => db.ServiceRequests
                    .Where(r => r.CreatedAt < cutoff && r.Status == ServiceRequestStatus.Closed)
                    .ExecuteDeleteAsync(ct)),
            (RetentionTableKeys.ProviderAvailabilities,
                () => db.ProviderAvailabilities.Where(p => p.ExpiresAt < cutoff).ExecuteDeleteAsync(ct)),
            (RetentionTableKeys.WhatsappContacts,
                () => db.WhatsappContacts.Where(c => c.LastInboundAt < cutoff).ExecuteDeleteAsync(ct)),
            (RetentionTableKeys.GeocodeCache,
                () => db.GeocodeCache.Where(g => g.FetchedAt < cutoff).ExecuteDeleteAsync(ct)),
            (RetentionTableKeys.ProviderRegistrationDrafts,
                () => db.RegistrationDrafts.Where(d => d.UpdatedAt < cutoff).ExecuteDeleteAsync(ct)),
            (RetentionTableKeys.ClientRequestDrafts,
                () => db.ClientRequestDrafts.Where(d => d.UpdatedAt < cutoff).ExecuteDeleteAsync(ct)),
            (RetentionTableKeys.AmbiguousIntentDrafts,
                () => db.AmbiguousIntentDrafts.Where(d => d.CreatedAt < cutoff).ExecuteDeleteAsync(ct)),
            (RetentionTableKeys.MatchFeedback,
                () => db.MatchFeedback.Where(f => f.PromptedAt < cutoff).ExecuteDeleteAsync(ct)),
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

            foreach (var (key, run) in sweeps)
            {
                try
                {
                    counts[key] = await run();
                    HookMetrics.RetentionDeleted.Add(
                        counts[key],
                        new KeyValuePair<string, object?>("table", key));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Retention sweep failed for {Table}; skipping", key);
                    counts[key] = -1;
                    HookMetrics.RetentionSweepErrors.Add(
                        1, new KeyValuePair<string, object?>("table", key));
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
            "Retention sweep complete: cutoff={Cutoff} total_deleted={Total} per_table={@PerTable} duration_ms={DurationMs}",
            cutoff, total, counts, sw.Elapsed.TotalMilliseconds);

        return new RetentionSweepResult(new ReadOnlyDictionary<string, int>(counts));
    }
}
