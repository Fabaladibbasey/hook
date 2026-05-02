namespace Hook.Features.ChatSession.AccessLog;

public class ChatAccessLog
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ChatId { get; init; }
    public required Guid ParticipantId { get; init; }
    public DateTimeOffset OpenedAt { get; init; } = DateTimeOffset.UtcNow;
    public string IpAddress { get; init; } = string.Empty;
    public string DeviceInfo { get; init; } = string.Empty;
}
