using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Observability;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.Whatsapp.ReceiveWebhook.ClassifyInboundIntent;

public sealed class ClassifyInboundIntentHandler(
    IConversationAi ai,
    ILogger<ClassifyInboundIntentHandler> logger)
{
    // [NonTransactional] keeps the Npgsql connection unpinned across the 60-150s
    // Ollama DetectIntent window. The classification result feeds bus.InvokeAsync
    // into the transactional RouteClassifiedIntent handler so durable outbox writes
    // happen inside an EF transaction.
    [NonTransactional]
    public async Task Handle(ClassifyInboundIntentRequested evt, IMessageBus bus, CancellationToken ct)
    {
        IntentDetectionResult detected;
        try
        {
            detected = await ai.DetectIntentAsync(evt.Message.Text ?? string.Empty, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Intent classification failed for {MessageId}; treating as Unknown",
                evt.Message.MessageId);
            HookMetrics.AiClassifyFailures.Add(1);
            detected = new IntentDetectionResult(IntentKind.Unknown, 0, "en", "exception");
        }

        await bus.InvokeAsync(new RouteClassifiedIntent(evt.Message, detected), ct);
    }
}
