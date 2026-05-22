using Hook.Features.Ai;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.Whatsapp.ReceiveWebhook.ClassifyInboundIntent;

public sealed class ClassifyInboundIntentHandler(IConversationAi ai)
{
    // [NonTransactional] keeps the Npgsql connection unpinned across the 60-150s
    // Ollama DetectIntent window. The classification result feeds bus.InvokeAsync
    // into the transactional RouteClassifiedIntent handler so durable outbox writes
    // happen inside an EF transaction.
    [NonTransactional]
    public async Task Handle(ClassifyInboundIntentRequested evt, IMessageBus bus, CancellationToken ct)
    {
        var detected = await ai.DetectIntentAsync(evt.Message.Text ?? string.Empty, ct);
        await bus.InvokeAsync(new RouteClassifiedIntent(evt.Message, detected), ct);
    }
}
