using Hook.Features.ChatLifecycle.Events;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;

namespace Hook.Features.ChatLifecycle.ExpireChat;

public sealed class HardExpireHandler(
    IChatRepository chats,
    TimeProvider clock,
    ILogger<HardExpireHandler> logger)
{
    public async Task Handle(HardExpireCheck evt, IMessageBus bus, CancellationToken ct)
    {
        var session = await chats.GetSessionAsync(evt.ChatId, ct);
        if (session is null) return;

        if (session.Status == ChatSessionStatus.Active)
        {
            session.Expire(clock.GetUtcNow());
        }

        await bus.PublishAsync(new BroadcastChatEventRequested(
            evt.ChatId, ChatHubEvents.ChatExpired, new ChatExpiredPayload("24h-expired")));

        logger.LogInformation("Chat {ChatId} hard-expired", evt.ChatId);
    }
}
