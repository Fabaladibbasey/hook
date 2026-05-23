using Hook.Features.Ai;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.Whatsapp.ReceiveWebhook.ClassifyInboundIntent;

public sealed class ClassifyInboundIntentHandler(IConversationAi ai)
{
    // [NonTransactional] keeps the Npgsql connection unpinned across the 60-150s
    // Ollama DetectIntent window. bus.PublishAsync enqueues RouteClassifiedIntentCommand
    // to the durable outbox so the transactional apply-handler commits without
    // pinning the AI worker.
    [NonTransactional]
    public async Task Handle(ClassifyInboundIntentCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        var detected = await ai.DetectIntentAsync(cmd.Message.Text ?? string.Empty, ct);
        await bus.PublishAsync(new RouteClassifiedIntentCommand(cmd.Message, detected));
    }
}
