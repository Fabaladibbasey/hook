using Hook.Features.Ai.Models;

namespace Hook.Features.Ai;

public sealed class LazyIntent(IConversationAi ai, string text)
{
    private Task<IntentDetectionResult>? _task;

    public Task<IntentDetectionResult> GetAsync(CancellationToken ct = default) =>
        _task ??= ai.DetectIntentAsync(text, ct);
}
