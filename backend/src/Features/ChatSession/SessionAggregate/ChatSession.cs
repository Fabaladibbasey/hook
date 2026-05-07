namespace Hook.Features.ChatSession.SessionAggregate;

public class ChatSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ChatSessionStatus Status { get; private set; } = ChatSessionStatus.Active;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }

    public static ChatSession Create(TimeSpan ttl, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = now,
        ExpiresAt = now + ttl,
        LastActivityAt = now
    };

    public void Touch(DateTimeOffset now)
    {
        if (Status != ChatSessionStatus.Active) return;
        LastActivityAt = now;
    }

    public void End(DateTimeOffset now)
    {
        Status = ChatSessionStatus.Ended;
        ExpiresAt = now;
    }

    public void Expire(DateTimeOffset now)
    {
        Status = ChatSessionStatus.Expired;
        ExpiresAt = now;
    }

    public bool CanSendMessage(DateTimeOffset now) =>
        Status == ChatSessionStatus.Active && now < ExpiresAt;
}
