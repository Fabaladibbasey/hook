using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Hook.Features.Ai.Warmup;

// Fires a single DetectIntentAsync call against Ollama in the background at startup
// so the first user-visible inbound finds the model already loaded. Detached Task.Run
// keeps Kestrel from blocking on Ollama's 20-30s cold-load (qwen2.5:3b on CPU).
// Bypasses the /readyz probe deliberately: that probe runs on a strict 2s budget,
// far too tight to cover a cold model load. Warmup uses the full HttpClient
// TimeoutSeconds budget so it does not give up before Ollama finishes loading.
// WarmupCompletion lets Program.cs gate Kestrel bind on a bounded wait so the first
// inbound after deploy does not race the model cold-load. StopAsync grants the inner
// task a short grace period to cancel cleanly so the scope is not disposed mid-await.
public sealed class AiWarmupHostedService(
    IServiceProvider services,
    IHostApplicationLifetime appLifetime,
    IOptions<OllamaOptions> options,
    ILogger<AiWarmupHostedService> logger) : IHostedService
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);
    private readonly TaskCompletionSource _warmupCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _linkedCts;
    private Task? _runner;

    public Task WarmupCompletion => _warmupCompletion.Task;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, appLifetime.ApplicationStopping);
        var ct = _linkedCts.Token;
        var budget = TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds));

        _runner = Task.Run(async () =>
        {
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budgetCts.CancelAfter(budget);
            var sw = Stopwatch.StartNew();
            try
            {
                await using var scope = services.CreateAsyncScope();
                var ai = scope.ServiceProvider.GetRequiredService<IConversationAi>();
                _ = await ai.DetectIntentAsync("ping", budgetCts.Token);
                logger.LogInformation(
                    "AI warm-up complete after {ElapsedMs} ms", sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (OperationCanceledException)
            {
                logger.LogWarning(
                    "AI warm-up did not finish within {BudgetSeconds}s; first inbound may pay cold-start tax",
                    budget.TotalSeconds);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AI warm-up failed");
            }
            finally
            {
                _warmupCompletion.TrySetResult();
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
