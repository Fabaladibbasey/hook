using Hook.Features.ChatLifecycle.Events;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.SessionAggregate;
using Wolverine;

namespace Hook.Features.ChatLifecycle.EndChat;

public sealed class IdleEndHandler(
    IChatRepository chats,
    ILogger<IdleEndHandler> logger)
{
    public async Task Handle(IdleEndCheck evt, IMessageBus bus, CancellationToken ct)
    {
        var session = await chats.GetSessionAsync(evt.ChatId, ct);
        if (session is null || session.Status != ChatSessionStatus.Active) return;

        if (session.LastActivityAt > evt.LastActivityAt)
        {
            logger.LogDebug("Skipping idle end for {ChatId} — fresher activity", evt.ChatId);
            return;
        }

        await bus.PublishAsync(new EndChatCommand(evt.ChatId, EndChatReason.Idle, null));
    }
}
