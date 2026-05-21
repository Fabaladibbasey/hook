using Hook.Shared.Persistence;
using Npgsql;

namespace Hook.Shared.Retention;

// Wolverine creates wolverine.wolverine_dead_letters during its own host startup
// but does not index `sent_at` — the RetentionSweeper's daily DELETE would
// otherwise seqscan the table. This bootstrap issues a one-shot
// CREATE INDEX CONCURRENTLY IF NOT EXISTS on every boot; idempotent, runs outside
// a transaction (concurrent indexes cannot live in one), and tolerates the table
// being temporarily absent on a brand-new schema (it will be picked up next boot).
// `received_at` is a varchar holding the listener address URI, NOT a timestamp —
// `sent_at` is the timestamptz of envelope send time and the right axis for retention.
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to ensure Wolverine DLQ index {IndexName}; retention DELETE will fall back to seqscan",
                IndexName);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
