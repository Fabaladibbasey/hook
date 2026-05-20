using Microsoft.Extensions.Options;

namespace Hook.Features.ProviderAvailability.Dev;

// Runs DevProviderSeeder in the background at startup so dev / staging boot
// doesn't block on it. Production opts out by leaving DevProviderSeedOptions
// disabled; the check is re-applied here as a belt-and-braces safety so the
// service can be unconditionally registered. StopAsync grants the inner task
// a short grace period to cancel cleanly so the scope is not disposed mid-await.
public sealed class DevProviderSeederHostedService(
    IServiceProvider services,
    IHostApplicationLifetime appLifetime,
    IOptions<DevProviderSeedOptions> options,
    ILogger<DevProviderSeederHostedService> logger) : IHostedService
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? _linkedCts;
    private Task? _runner;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = options.Value;
        if (!opts.Enabled || !opts.AutoSeed)
        {
            return Task.CompletedTask;
        }

        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, appLifetime.ApplicationStopping);
        var ct = _linkedCts.Token;

        _runner = Task.Run(async () =>
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var seeder = scope.ServiceProvider.GetRequiredService<DevProviderSeeder>();
                await seeder.SeedAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dev provider seeding failed");
            }
        }, ct);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_runner is null) return;
        _linkedCts?.Cancel();
        await Task.WhenAny(_runner, Task.Delay(StopGrace, cancellationToken));
        _linkedCts?.Dispose();
    }
}
