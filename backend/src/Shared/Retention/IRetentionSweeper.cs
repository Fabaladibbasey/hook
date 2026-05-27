namespace Hook.Shared.Retention;

public interface IRetentionSweeper
{
    Task<RetentionSweepResult> RunOnceAsync(CancellationToken ct);
}

public sealed record RetentionSweepResult(IReadOnlyDictionary<string, int> CountsByTable);
