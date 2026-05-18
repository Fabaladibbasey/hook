using Hook.Shared.Domain;
using Hook.Shared.Pipeline.PostCommitSends;

namespace Hook.Features.ChatSession.SessionAggregate;

public class ChatSession : AggregateRoot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ChatSessionStatus Status { get; private set; } = ChatSessionStatus.Active;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }

    public static ChatSession Create(TimeSpan ttl, DateTimeOffset now) =>
        Create(Guid.NewGuid(), ttl, now);

    public static ChatSession Create(Guid id, TimeSpan ttl, DateTimeOffset now) => new()
    {
        Id = id,
        CreatedAt = now,
        ExpiresAt = now + ttl,
        LastActivityAt = now
    };

    public void Touch(DateTimeOffset now)
    {
        if (Status != ChatSessionStatus.Active) return;
        LastActivityAt = now;
    }

    public void End(DateTimeOffset now, string reason, string? endedBy = null)
    {
        Status = ChatSessionStatus.Ended;
        ExpiresAt = now;
        RaiseDomainEvent(new BroadcastChatEventRequested(
            Id, ChatHubEvents.ChatEnded, new ChatEndedPayload(reason, endedBy)));
    }

    public void Expire(DateTimeOffset now)
    {
        Status = ChatSessionStatus.Expired;
        ExpiresAt = now;
    }

    public bool CanSendMessage(DateTimeOffset now) =>
        Status == ChatSessionStatus.Active && now < ExpiresAt;
}
