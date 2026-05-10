using System.Globalization;
using Hook.Features.ChatSession;
using Hook.Features.ContactSharing.Events;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;

namespace Hook.Features.ChatPrivacyRouting.RouteMatch;

public sealed class ChatRoutingRequestedHandler(
    ChatSessionFactory factory,
    IMatchRepository matches,
    IWhatsappClient whatsapp,
    ILogger<ChatRoutingRequestedHandler> logger)
{
    public async Task Handle(ChatRoutingRequested evt, CancellationToken ct)
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

        var links = await factory.CreateAsync(evt.ClientPhone, evt.ProviderPhone, ct);
        match.ChatId = links.ChatId;
        await matches.SaveChangesAsync(ct);

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

        var sends = new List<Task>(2);
        if (PhoneNumber.TryParse(evt.ClientPhone, out var client))
        {
            sends.Add(whatsapp.SendTextAsync(client, clientBody, ct));
        }
        if (providerParsed)
        {
            sends.Add(whatsapp.SendTextAsync(provider, providerBody, ct));
        }
        if (sends.Count > 0) await Task.WhenAll(sends);

        logger.LogInformation("Chat routing complete for match {MatchId}, chat {ChatId}", evt.MatchId, links.ChatId);
    }
}
