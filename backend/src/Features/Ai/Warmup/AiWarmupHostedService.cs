namespace Hook.Features.Ai.Warmup;

// Fires AiReadinessProbe in the background at startup so the first real /readyz
// hit finds the 10s cache warm. Detached Task.Run keeps Kestrel from blocking
// on Ollama cold-start (20-30s for qwen2.5:3b on CPU). StopAsync grants the inner
// task a short grace period to cancel cleanly so the scope is not disposed mid-await.
public sealed class AiWarmupHostedService(
    IServiceProvider services,
    IHostApplicationLifetime appLifetime,
    ILogger<AiWarmupHostedService> logger) : IHostedService
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? _linkedCts;
    private Task? _runner;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, appLifetime.ApplicationStopping);
        var ct = _linkedCts.Token;

        _runner = Task.Run(async () =>
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var probe = scope.ServiceProvider.GetRequiredService<AiReadinessProbe>();
                var result = await probe.ProbeAsync(ct);
                logger.LogInformation(
                    "AI warm-up probe complete healthy={Healthy} error={Error}",
                    result.Healthy, result.Error);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AI warm-up probe failed");
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
