using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.ProviderAvailability.Refresh;

public sealed class ProviderRefreshCheckHandler(
    IProviderAvailabilityRepository availability,
    IConversationAi ai,
    IMessageBus bus,
    ILogger<ProviderRefreshCheckHandler> logger)
{
    // [NonTransactional]: AI inference takes 60-150s; opt out of AutoApplyTransactions
    // so the handler doesn't pin an Npgsql connection across the Ollama window.
    [NonTransactional]
    public async Task Handle(ProviderRefreshCheck evt, CancellationToken ct)
    {
        var provider = await availability.GetAsync(evt.Phone, ct);
        if (provider is null) return;

        if (provider.LastActiveAt > evt.LastActiveAt)
        {
            logger.LogDebug("Skipping refresh prompt for {Phone}: fresher activity",
                PhoneNumber.TryParse(provider.Phone, out var ph) ? ph.Mask() : provider.Phone);
            return;
        }

        if (!PhoneNumber.TryParse(provider.Phone, out var phone)) return;

        var ctx = new ReplyContext(
            Purpose: "provider-availability-check",
            RecentTurns: [],
            LanguageHint: "en")
        {
            Facts = new Dictionary<string, string>
            {
                ["services"] = string.Join(", ", provider.Services),
                ["instruction"] = "Ask the provider whether they are still available. Mention they can reply YES to stay listed."
            }
        };

        var reply = await AiReplyHelper.TryGenerateAsync(
            ai, ctx, "provider_refresh", logger, ct, AiReplyHelper.NonCriticalReplyTimeout);
        if (reply is null) return;

        await bus.PublishAsync(new SendWhatsAppTextRequested(phone, reply));
        logger.LogInformation("Sent 22h refresh prompt to {Phone}", phone.Mask());
    }
}
