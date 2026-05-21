namespace Hook.Shared.Hosting;

// Shared lifecycle plumbing for one-shot startup background tasks (AI warm-up,
// dev provider seeder, future similar). Subclasses override RunAsync and
// optionally ShouldRun. StopAsync grants StopGrace seconds for the inner task
// to cancel cleanly so the scope is not disposed mid-await. IHost.Dispose calls
// StopAsync a second time after the initial host shutdown; the Interlocked
// exchange makes the second pass a no-op instead of throwing
// ObjectDisposedException on the disposed source.
public abstract class BackgroundOnceHostedService(
    IServiceProvider services,
    IHostApplicationLifetime appLifetime) : IHostedService
{
    protected static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? _linkedCts;
    private Task? _runner;

    protected virtual bool ShouldRun() => true;

    protected abstract Task RunAsync(IServiceProvider services, CancellationToken ct);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!ShouldRun()) return Task.CompletedTask;

        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, appLifetime.ApplicationStopping);
        var ct = _linkedCts.Token;

        // Don't pass `ct` to Task.Run: that would cancel SCHEDULING of the body
        // if the token were already cancelled (or cancelled before the body ran),
        // skipping our outcome-handling try/catch (e.g. AiWarmup's TrySetResult
        // on shutdown). The body itself observes cancellation via the parameter.
        _runner = Task.Run(() => RunAsync(services, ct));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var cts = Interlocked.Exchange(ref _linkedCts, null);
        if (cts is null || _runner is null) return;
        cts.Cancel();
        await Task.WhenAny(_runner, Task.Delay(StopGrace, cancellationToken));
        cts.Dispose();
    }
}
