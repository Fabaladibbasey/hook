using Hook.Features.ChatLifecycle.Events;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.SessionAggregate;

namespace Hook.Features.ChatLifecycle.EndChat;

public sealed class IdleEndHandler(
    IChatRepository chats,
    TimeProvider clock,
    ILogger<IdleEndHandler> logger)
{
    public async Task Handle(IdleEndCheck evt, CancellationToken ct)
    {
        var session = await chats.GetSessionAsync(evt.ChatId, ct);
        if (session is null || session.Status != ChatSessionStatus.Active) return;

        if (session.LastActivityAt > evt.LastActivityAt)
        {
            logger.LogDebug("Skipping idle end for {ChatId} — fresher activity", evt.ChatId);
            return;
        }

        session.End(clock.GetUtcNow(), "idle");
    }
}
