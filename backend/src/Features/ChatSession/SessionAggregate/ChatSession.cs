using Hook.Features.ChatLifecycle.EndChat;
using Hook.Shared.Domain;
using Hook.Shared.Pipeline.PostCommitSends;

namespace Hook.Features.ChatSession.SessionAggregate;

public class ChatSession : AggregateRoot
{
    public Guid Id { get; init; }
    public ChatSessionStatus Status { get; private set; } = ChatSessionStatus.Active;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }

    public static ChatSession Create(TimeSpan ttl, DateTimeOffset now) =>
        Create(Guid.CreateVersion7(), ttl, now);

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

    public void End(DateTimeOffset now, EndChatReason reason, string endedBy = "")
    {
        Status = ChatSessionStatus.Ended;
        ExpiresAt = now;
        RaiseDomainEvent(new BroadcastChatEvent(
            Id, ChatHubEvents.ChatEnded, new ChatEndedPayload(reason.ToWire(), endedBy)));
    }

    public void HardExpire(DateTimeOffset now)
    {
        if (Status != ChatSessionStatus.Active)
            throw new InvalidOperationException($"ChatSession {Id} cannot be hard-expired from {Status}");
        Status = ChatSessionStatus.Expired;
        ExpiresAt = now;
        RaiseDomainEvent(new BroadcastChatEvent(
            Id, ChatHubEvents.ChatExpired, new ChatExpiredPayload("24h-expired")));
    }

    // No state mutation: idle reminders can fire repeatedly; dedup lives in IdleReminderHandler.
    public void SendIdleReminder() =>
        RaiseDomainEvent(new BroadcastChatEvent(
            Id, ChatHubEvents.IdleReminder,
            new IdleReminderPayload("Are you still available? Reply to continue.")));

    public bool CanSendMessage(DateTimeOffset now) =>
        Status == ChatSessionStatus.Active && now < ExpiresAt;
}
