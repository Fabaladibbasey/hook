namespace Hook.Features.RateLimiting;

// Drives Sweep on every registered ISweepableLimiter at the configured cadence.
// One ticker bounds the per-key map of every singleton limiter so memory stays
// proportional to active key cardinality, not ever-seen.
internal sealed class LimiterEvictionHostedService(
    IEnumerable<ISweepableLimiter> limiters,
    ILogger<LimiterEvictionHostedService> log) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var limiter in limiters)
                {
                    var removed = limiter.SweepIdle();
                    if (removed > 0)
                        log.LogDebug("limiter sweep dropped {Count} idle keys from {Limiter}",
                            removed, limiter.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
