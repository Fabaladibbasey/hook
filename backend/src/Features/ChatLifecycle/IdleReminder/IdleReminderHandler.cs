using Hook.Features.ChatLifecycle.Events;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;

namespace Hook.Features.ChatLifecycle.IdleReminder;

public sealed class IdleReminderHandler(
    IChatRepository chats,
    ILogger<IdleReminderHandler> logger)
{
    public async Task Handle(IdleReminderCheck evt, IMessageBus bus, CancellationToken ct)
    {
        var session = await chats.GetSessionAsync(evt.ChatId, ct);
        if (session is null || session.Status != ChatSessionStatus.Active) return;

        if (session.LastActivityAt > evt.LastActivityAt)
        {
            logger.LogDebug("Skipping idle reminder for {ChatId} — fresher activity", evt.ChatId);
            return;
        }

        await bus.PublishAsync(new BroadcastChatEventRequested(
            evt.ChatId, ChatHubEvents.IdleReminder,
            new IdleReminderPayload("Are you still available? Reply to continue.")));
    }
}
