using Hook.Features.ChatLifecycle.Events;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.SessionAggregate;
using Microsoft.AspNetCore.SignalR;

namespace Hook.Features.ChatLifecycle.ExpireChat;

public sealed class HardExpireHandler(
    IChatRepository chats,
    IHubContext<ChatHub> hub,
    ILogger<HardExpireHandler> logger)
{
    public async Task Handle(HardExpireCheck evt, CancellationToken ct)
    {
        var session = await chats.GetSessionAsync(evt.ChatId, ct);
        if (session is null) return;

        if (session.Status == ChatSessionStatus.Active)
        {
            session.Expire();
            await chats.SaveChangesAsync(ct);
        }

        await hub.Clients.Group(ChatHub.ChatGroup(evt.ChatId)).SendAsync("ChatExpired",
            new { reason = "24h-expired" }, ct);

        logger.LogInformation("Chat {ChatId} hard-expired", evt.ChatId);
    }
}
