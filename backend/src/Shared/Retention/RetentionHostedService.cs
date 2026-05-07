using Microsoft.Extensions.Options;

namespace Hook.Shared.Retention;

public sealed class RetentionHostedService(
    IServiceScopeFactory scopes,
    IOptionsMonitor<RetentionOptions> options,
    TimeProvider clock,
    ILogger<RetentionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.Enabled)
        {
            logger.LogInformation("RetentionHostedService disabled by config");
            return;
        }

        try
        {
            await Task.Delay(options.CurrentValue.StartupDelay, clock, stoppingToken);
            await RunOnceSafelyAsync(stoppingToken);

            using var timer = new PeriodicTimer(options.CurrentValue.SweepInterval, clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!options.CurrentValue.Enabled) continue;
                await RunOnceSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("RetentionHostedService stopping");
        }
    }

    private async Task RunOnceSafelyAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sweeper = scope.ServiceProvider.GetRequiredService<IRetentionSweeper>();
            await sweeper.RunOnceAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Retention sweep failed; will retry on next tick");
        }
    }
}
