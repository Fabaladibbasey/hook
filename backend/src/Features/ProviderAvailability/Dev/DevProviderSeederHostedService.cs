using Hook.Shared.Hosting;
using Microsoft.Extensions.Options;

namespace Hook.Features.ProviderAvailability.Dev;

// Runs DevProviderSeeder in the background at startup so dev / staging boot
// doesn't block on it. Production opts out by leaving DevProviderSeedOptions
// disabled; the check is re-applied here as a belt-and-braces safety so the
// service can be unconditionally registered.
public sealed class DevProviderSeederHostedService(
    IServiceProvider services,
    IHostApplicationLifetime appLifetime,
    IOptions<DevProviderSeedOptions> options,
    ILogger<DevProviderSeederHostedService> logger) : BackgroundOnceHostedService(services, appLifetime)
{
    private readonly IServiceProvider _services = services;
    private readonly IOptions<DevProviderSeedOptions> _options = options;
    private readonly ILogger<DevProviderSeederHostedService> _logger = logger;

    protected override bool ShouldRun()
    {
        var opts = _options.Value;
        return opts.Enabled && opts.AutoSeed;
    }

    protected override async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var seeder = scope.ServiceProvider.GetRequiredService<DevProviderSeeder>();
            await seeder.SeedAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dev provider seeding failed");
        }
    }
}
