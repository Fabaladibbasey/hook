using Hook.Features.ChatLifecycle.EndChat;
using Hook.Shared.Domain;
using Hook.Shared.Pipeline.PostCommitSends;

namespace Hook.Features.ChatSession.SessionAggregate;

public class ChatSession : AggregateRoot
{
    public Guid Id { get; private init; }
    public ChatSessionStatus Status { get; private set; } = ChatSessionStatus.Active;
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }
    public DateTimeOffset? ProductiveSilenceFiredAt { get; private set; }

    /// <summary>
    /// Optimistic-concurrency token: every state mutation bumps this so the second
    /// of two racing scopes cannot commit silently — the loser hits a concurrency
    /// exception which AcceptChatMessageHandler surfaces as <c>SessionEnded</c>.
    /// </summary>
    public int Version { get; private set; }

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
        Version += 1;
    }

    public void End(DateTimeOffset now, EndChatReason reason, string endedBy = "")
    {
        if (Status != ChatSessionStatus.Active) return;
        // Validate before any mutation so an unmapped reason cannot leave the
        // aggregate Ended with only the BroadcastChatEvent queued.
        var endReason = MapEndReason(reason);
        Status = ChatSessionStatus.Ended;
        ExpiresAt = now;
        Version += 1;
        RaiseDomainEvent(new BroadcastChatEvent(
            Id, ChatHubEvents.ChatEnded, new ChatEndedPayload(reason.ToWire(), endedBy)));
        RaiseDomainEvent(new ChatSessionEndedEvent(Id, endReason));
    }

    public void HardExpire(DateTimeOffset now)
    {
        if (Status != ChatSessionStatus.Active)
            throw new InvalidOperationException($"ChatSession {Id} cannot be hard-expired from {Status}");
        Status = ChatSessionStatus.Expired;
        ExpiresAt = now;
        Version += 1;
        RaiseDomainEvent(new BroadcastChatEvent(
            Id, ChatHubEvents.ChatExpired, new ChatExpiredPayload("24h-expired")));
        RaiseDomainEvent(new ChatSessionEndedEvent(Id, ChatEndReason.Expired));
    }

    private static ChatEndReason MapEndReason(EndChatReason reason) => reason switch
    {
        EndChatReason.User => ChatEndReason.User,
        EndChatReason.Idle => ChatEndReason.Idle,
        // AlreadyEnded is a UI response-state, never passed to End(). Defensive throw.
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Cannot map to ChatEndReason")
    };

    // No state mutation: idle reminders can fire repeatedly; dedup lives in IdleReminderHandler.
    public void SendIdleReminder() =>
        RaiseDomainEvent(new BroadcastChatEvent(
            Id, ChatHubEvents.IdleReminder,
            new IdleReminderPayload("Are you still available? Reply to continue.")));

    public bool CanSendMessage(DateTimeOffset now) =>
        Status == ChatSessionStatus.Active && now < ExpiresAt;
}
