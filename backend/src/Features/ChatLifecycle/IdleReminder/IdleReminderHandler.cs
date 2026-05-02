using Hook.Features.ChatLifecycle.Events;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.SessionAggregate;
using Microsoft.AspNetCore.SignalR;

namespace Hook.Features.ChatLifecycle.IdleReminder;

public sealed class IdleReminderHandler(
    IChatRepository chats,
    IHubContext<ChatHub> hub,
    ILogger<IdleReminderHandler> logger)
{
    public async Task Handle(IdleReminderCheck evt, CancellationToken ct)
    {
        var session = await chats.GetSessionAsync(evt.ChatId, ct);
        if (session is null || session.Status != ChatSessionStatus.Active) return;

        if (session.LastActivityAt > evt.LastActivityAt)
        {
            logger.LogDebug("Skipping idle reminder for {ChatId} — fresher activity", evt.ChatId);
            return;
        }

        await hub.Clients.Group(ChatHub.GroupName(evt.ChatId)).SendAsync(
            "IdleReminder",
            new { message = "Are you still available? Reply to continue." },
            ct);
    }
}
