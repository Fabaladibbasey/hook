using Hook.Features.ChatLifecycle.Events;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.SessionAggregate;

namespace Hook.Features.ChatLifecycle.ExpireChat;

public sealed class HardExpireHandler(
    IChatRepository chats,
    TimeProvider clock,
    ILogger<HardExpireHandler> logger)
{
    public async Task Handle(HardExpireCheck evt, CancellationToken ct)
    {
        var session = await chats.GetSessionAsync(evt.ChatId, ct);
        if (session is null) return;

        if (session.Status == ChatSessionStatus.Active)
        {
            session.HardExpire(clock.GetUtcNow());
            logger.LogInformation("Chat {ChatId} hard-expired", evt.ChatId);
        }
    }
}
