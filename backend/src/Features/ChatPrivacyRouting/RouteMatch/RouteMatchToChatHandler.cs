using System.Globalization;
using Hook.Features.ChatSession;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Hook.Shared.Whatsapp;
using Wolverine;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;

namespace Hook.Features.ChatPrivacyRouting.RouteMatch;

public sealed class RouteMatchToChatHandler(
    ChatSessionFactory factory,
    IMatchRepository matches,
    ILogger<RouteMatchToChatHandler> logger)
{
    public async Task Handle(RouteMatchToChatCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        var match = await matches.GetAsync(cmd.MatchId, ct);
        if (match is null)
        {
            logger.LogWarning("ChatRouting: match {MatchId} not found", cmd.MatchId);
            return;
        }
        if (match.ChatId is not null)
        {
            logger.LogDebug("ChatRouting: match {MatchId} already routed to chat {ChatId}", cmd.MatchId, match.ChatId);
            return;
        }

        // Reserve the claim with a pre-allocated ChatId before allocating the session
        // + 2 ChatParticipant rows. Race-loser exits with zero writes; the previous
        // order rolled back 4-5 inserts through the AutoApplyTransactions tx.
        var chatId = Guid.CreateVersion7();
        if (!await matches.TryClaimChatRoutingAsync(match.Id, chatId, ct))
        {
            logger.LogDebug("ChatRouting: match {MatchId} lost the claim — peer already routed", cmd.MatchId);
            return;
        }

        var links = await factory.CreateAsync(chatId, cmd.ClientPhone, cmd.ProviderPhone, ct);

        var mapsUrl = string.Format(
            CultureInfo.InvariantCulture,
            "https://maps.google.com/?q={0},{1}",
            cmd.RequesterLatitude,
            cmd.RequesterLongitude);

        var providerParsed = PhoneNumber.TryParse(cmd.ProviderPhone, out var provider);
        var maskedProvider = providerParsed ? provider.Mask() : cmd.ProviderPhone;
        var prefix = $"Match #{cmd.MatchPosition} ({maskedProvider}): ";
        var clientText = cmd.ClientConsented
            ? $"{prefix}the other party prefers a private chat. Open: {links.ClientUrl}"
            : $"{prefix}your private chat is ready. Open: {links.ClientUrl}";
        var providerIntro = $"{match.ServiceSlug} client at {cmd.RequesterAddress} ({mapsUrl})";
        var providerText = cmd.ProviderConsented
            ? $"{providerIntro} prefers a private chat. Open: {links.ProviderUrl}"
            : $"{providerIntro} wants to chat. Open: {links.ProviderUrl}";
        providerText = RequestDetailsFormatter.AppendIfPresent(providerText, cmd.Description);

        if (PhoneNumber.TryParse(cmd.ClientPhone, out var client))
            await bus.PublishAsync(new SendWhatsAppTextCommand(client, clientText));
        if (providerParsed)
            await bus.PublishAsync(new SendWhatsAppTextCommand(provider, providerText));

        logger.LogInformation("Chat routing complete for match {MatchId}, chat {ChatId}", cmd.MatchId, links.ChatId);
    }
}
