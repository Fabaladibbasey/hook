namespace Hook.Features.ChatSession.ParticipantAggregate;

public class ChatDeviceKey
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ChatId { get; init; }
    public required Guid ParticipantId { get; init; }
    public required Guid DeviceId { get; init; }
    public required byte[] PublicKey { get; set; }
    public long LastInboundSequence { get; private set; }
    public DateTimeOffset FirstSeenAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public bool TryAdvanceSequence(long sequence)
    {
        if (sequence <= LastInboundSequence) return false;
        LastInboundSequence = sequence;
        return true;
    }
}
