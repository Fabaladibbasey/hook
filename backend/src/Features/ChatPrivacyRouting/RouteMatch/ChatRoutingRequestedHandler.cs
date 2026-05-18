using System.Globalization;
using Hook.Features.ChatSession;
using Hook.Features.ContactSharing.Events;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;

namespace Hook.Features.ChatPrivacyRouting.RouteMatch;

public sealed class ChatRoutingRequestedHandler(
    ChatSessionFactory factory,
    IMatchRepository matches,
    ILogger<ChatRoutingRequestedHandler> logger)
{
    public async Task Handle(ChatRoutingRequested evt, IMessageBus bus, CancellationToken ct)
    {
        var match = await matches.GetAsync(evt.MatchId, ct);
        if (match is null)
        {
            logger.LogWarning("ChatRouting: match {MatchId} not found", evt.MatchId);
            return;
        }
        if (match.ChatId is not null)
        {
            logger.LogDebug("ChatRouting: match {MatchId} already routed to chat {ChatId}", evt.MatchId, match.ChatId);
            return;
        }

        // Reserve the claim with a pre-allocated ChatId before allocating the session
        // + 2 ChatParticipant rows. Race-loser exits with zero writes; the previous
        // order rolled back 4-5 inserts through the AutoApplyTransactions tx.
        var chatId = Guid.NewGuid();
        if (!await matches.TryClaimChatRoutingAsync(match.Id, chatId, ct))
        {
            logger.LogDebug("ChatRouting: match {MatchId} lost the claim — peer already routed", evt.MatchId);
            return;
        }

        var links = await factory.CreateAsync(chatId, evt.ClientPhone, evt.ProviderPhone, ct);

        var mapsUrl = string.Format(
            CultureInfo.InvariantCulture,
            "https://maps.google.com/?q={0},{1}",
            evt.RequesterLatitude,
            evt.RequesterLongitude);

        var providerParsed = PhoneNumber.TryParse(evt.ProviderPhone, out var provider);
        var maskedProvider = providerParsed ? provider.Mask() : evt.ProviderPhone;
        var prefix = $"Match #{evt.MatchPosition} ({maskedProvider}): ";
        var clientBody = evt.ClientConsented
            ? $"{prefix}the other party prefers a private chat. Open: {links.ClientUrl}"
            : $"{prefix}your private chat is ready. Open: {links.ClientUrl}";
        var providerBody = evt.ProviderConsented
            ? $"{match.ServiceSlug} client at {evt.RequesterAddress} ({mapsUrl}) prefers a private chat. Open: {links.ProviderUrl}"
            : $"{match.ServiceSlug} client at {evt.RequesterAddress} ({mapsUrl}) wants to chat. Open: {links.ProviderUrl}";

        if (PhoneNumber.TryParse(evt.ClientPhone, out var client))
            await bus.PublishAsync(new SendWhatsAppTextRequested(client, clientBody));
        if (providerParsed)
            await bus.PublishAsync(new SendWhatsAppTextRequested(provider, providerBody));

        logger.LogInformation("Chat routing complete for match {MatchId}, chat {ChatId}", evt.MatchId, links.ChatId);
    }
}
