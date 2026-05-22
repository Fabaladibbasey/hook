using System.Diagnostics;
using Hook.Shared.Hosting;
using Microsoft.Extensions.Options;

namespace Hook.Features.Ai.Warmup;

// Fires a single DetectIntentAsync call against Ollama in the background at startup
// so the first user-visible inbound finds the model already loaded. Detached Task.Run
// keeps Kestrel from blocking on Ollama's 20-30s cold-load (qwen2.5:3b on CPU).
// Bypasses the /readyz probe deliberately: that probe runs on a strict 2s budget,
// far too tight to cover a cold model load. Warmup uses the full HttpClient
// TimeoutSeconds budget so it does not give up before Ollama finishes loading.
// WarmupCompletion lets Program.cs gate Kestrel bind on a bounded wait so the first
// inbound after deploy does not race the model cold-load. The TCS resolves ONLY on
// successful warm-up or shutdown — budget elapsed and transport failure leave it
// unresolved so the gate runs to 15s and the cold-start warning fires.
public sealed class AiWarmupHostedService(
    IServiceProvider services,
    IHostApplicationLifetime appLifetime,
    IOptions<OllamaOptions> options,
    ILogger<AiWarmupHostedService> logger) : BackgroundOnceHostedService(services, appLifetime)
{
    private readonly IServiceProvider _services = services;
    private readonly IOptions<OllamaOptions> _options = options;
    private readonly ILogger<AiWarmupHostedService> _logger = logger;
    private readonly TaskCompletionSource _warmupCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WarmupCompletion => _warmupCompletion.Task;

    protected override async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
        var budget = TimeSpan.FromSeconds(Math.Max(1, _options.Value.TimeoutSeconds));
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(budget);
        var sw = Stopwatch.StartNew();
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var ai = scope.ServiceProvider.GetRequiredService<IConversationAi>();
            await ai.PingAsync(budgetCts.Token);
            _logger.LogInformation(
                "AI warm-up complete after {ElapsedMs} ms", sw.ElapsedMilliseconds);
            _warmupCompletion.TrySetResult();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host is shutting down — resolve so any pending awaiter does not
            // block the shutdown path. No first-inbound implication.
            _warmupCompletion.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            // Budget elapsed before the cold-load finished. Leave the TCS
            // unresolved so Program.cs's 15s Delay wins and the warning fires.
            _logger.LogWarning(
                "AI warm-up did not finish within {BudgetSeconds}s; first inbound may pay cold-start tax",
                budget.TotalSeconds);
        }
        catch (Exception ex)
        {
            // Ollama unreachable / other transport failure — same intent: leave
            // unresolved so the gate runs to 15s and the warning fires.
            _logger.LogWarning(ex, "AI warm-up failed");
        }
    }
}
