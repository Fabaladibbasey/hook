using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.ProviderAvailability.Refresh;

public sealed class ProviderRefreshCheckHandler(
    IProviderAvailabilityRepository availability,
    IConversationAi ai,
    IWhatsappClient whatsapp,
    ILogger<ProviderRefreshCheckHandler> logger)
{
    public async Task Handle(ProviderRefreshCheck evt, CancellationToken ct)
    {
        var provider = await availability.GetAsync(evt.Phone, ct);
        if (provider is null) return;

        if (provider.LastActiveAt > evt.LastActiveAt)
        {
            logger.LogDebug("Skipping refresh prompt for {Phone}: fresher activity", PhoneNumber.TryParse(provider.Phone, out var ph) ? ph.Mask() : provider.Phone);
            return;
        }

        if (!PhoneNumber.TryParse(provider.Phone, out var phone)) return;

        var ctx = new ReplyContext(
            Purpose: "provider-availability-check",
            RecentTurns: [],
            LanguageHint: "en",
            Facts: new Dictionary<string, string>
            {
                ["services"] = string.Join(", ", provider.Services),
                ["instruction"] = "Ask the provider whether they are still available. Mention they can reply YES to stay listed."
            });

        var reply = await AiReplyHelper.TryGenerateAsync(ai, ctx, "provider_refresh", logger, ct);
        if (reply is null) return;

        await whatsapp.SendTextAsync(phone, reply, ct);
        logger.LogInformation("Sent 22h refresh prompt to {Phone}", phone.Mask());
    }
}
