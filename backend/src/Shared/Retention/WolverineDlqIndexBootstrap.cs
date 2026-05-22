using Hook.Features.Observability;
using Hook.Shared.Persistence;
using Npgsql;

namespace Hook.Shared.Retention;

// Wolverine creates wolverine.wolverine_dead_letters during its own host startup
// but does not index `sent_at` — the RetentionSweeper's daily DELETE would
// otherwise seqscan the table. This bootstrap issues a one-shot
// CREATE INDEX CONCURRENTLY IF NOT EXISTS on every boot; idempotent, runs outside
// a transaction (concurrent indexes cannot live in one). `received_at` is a varchar
// holding the listener address URI, NOT a timestamp — `sent_at` is the timestamptz
// of envelope send time and the right axis for retention.
//
// Tolerates the table being temporarily absent (UndefinedTable / 42P01) — e.g. a
// brand-new schema where Wolverine's own bootstrap hasn't run yet, or a registration
// ordering quirk under xUnit cold start. Logs + increments a metric and lets host
// startup continue; the next boot re-tries idempotently.
public sealed class WolverineDlqIndexBootstrap(
    NpgsqlDataSource dataSource,
    ILogger<WolverineDlqIndexBootstrap> logger) : IHostedService
{
    public const string IndexName = "ix_wolverine_dlq_sent_at";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var sql = $"CREATE INDEX CONCURRENTLY IF NOT EXISTS {IndexName} " +
                  $"ON {WolverineConfig.Schema}.{RetentionTableKeys.WolverineDeadLetters} (sent_at);";

        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            logger.LogInformation("Wolverine DLQ index ensured ({IndexName})", IndexName);
        }
        catch (PostgresException ex) when (
            ex.SqlState == PostgresErrorCodes.UndefinedTable ||
            ex.SqlState == PostgresErrorCodes.InvalidSchemaName)
        {
            HookMetrics.DlqIndexBootstrapMissedRetries.Add(1);
            logger.LogWarning(ex,
                "Wolverine DLQ table/schema not yet present; index bootstrap will retry next boot");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
