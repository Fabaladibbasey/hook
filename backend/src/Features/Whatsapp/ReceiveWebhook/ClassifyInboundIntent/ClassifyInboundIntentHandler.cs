using Hook.Features.Ai;
using Hook.Features.Ai.PlatformQa;
using Hook.Features.MetaTemplates;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.Whatsapp.ReceiveWebhook.ClassifyInboundIntent;

public sealed class ClassifyInboundIntentHandler(IConversationAi ai, IWhatsappContactRepository contacts)
{
    // [NonTransactional] keeps the Npgsql connection unpinned across the 60-150s
    // Ollama DetectIntent window. bus.PublishAsync enqueues RouteClassifiedIntentCommand
    // to the durable outbox so the transactional apply-handler commits without
    // pinning the AI worker. UpdatePreferredLocaleAsync uses raw SQL UPDATE so it
    // does not pin a connection either — orthogonal to the AI window.
    [NonTransactional]
    public async Task Handle(ClassifyInboundIntentCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        var detected = await ai.DetectIntentAsync(cmd.Message.Text ?? string.Empty, ct);
        var sanitized = LocaleValidator.Sanitize(detected.LanguageCode);
        await contacts.UpdatePreferredLocaleAsync(cmd.Message.From.Value, sanitized, ct);
        await bus.PublishAsync(new RouteClassifiedIntentCommand(cmd.Message, detected));
    }
}
