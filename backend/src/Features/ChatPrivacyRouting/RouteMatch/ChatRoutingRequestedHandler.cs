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

        var sends = new List<Task>(2);
        if (PhoneNumber.TryParse(evt.ClientPhone, out var client))
        {
            sends.Add(whatsapp.SendTextAsync(client,
                $"The other party prefers a private chat. Open: {links.ClientUrl}", ct));
        }
        if (PhoneNumber.TryParse(evt.ProviderPhone, out var provider))
        {
            sends.Add(whatsapp.SendTextAsync(provider,
                $"A client wants to chat with you. Open: {links.ProviderUrl}", ct));
        }
        if (sends.Count > 0) await Task.WhenAll(sends);

        logger.LogInformation("Chat routing complete for match {MatchId}, chat {ChatId}", evt.MatchId, links.ChatId);
    }
}
