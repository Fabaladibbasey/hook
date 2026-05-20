using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.Whatsapp.ReceiveWebhook.ColdReply;

public sealed class SendColdReplyHandler(
    IConversationAi ai,
    IMessageBus bus,
    ILogger<SendColdReplyHandler> logger)
{
    // [NonTransactional]: AI inference takes 60-150s; opt out of AutoApplyTransactions
    // so the handler doesn't pin an Npgsql connection across the Ollama window.
    [NonTransactional]
    public async Task Handle(SendColdReplyRequested evt, CancellationToken ct)
    {
        var ctx = new ReplyContext(
            Purpose: evt.Purpose,
            RecentTurns: [new ConversationTurn(TurnRole.User, evt.Text)],
            LanguageHint: evt.Detected.LanguageCode)
        {
            Facts = new Dictionary<string, string>
            {
                ["intent"] = evt.Detected.Intent.ToString()
            }
        };
        var fallback = evt.Purpose == "greeting-reply"
            ? "Hi! I connect people with local service providers. REQUEST a service if you need help, or REGISTER as a provider if you offer one."
            : "I help connect people who need services with providers. Reply REQUEST if you need help, or REGISTER if you offer a service.";
        var reply = await AiReplyHelper.TryGenerateOrFallbackAsync(ai, ctx, evt.Purpose, fallback, logger, ct);
        await bus.PublishAsync(new SendWhatsAppTextRequested(evt.To, reply));
    }
}
