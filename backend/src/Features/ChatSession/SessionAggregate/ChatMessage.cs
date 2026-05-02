namespace Hook.Features.ChatSession.SessionAggregate;

public class ChatMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ChatId { get; init; }
    public required Guid ParticipantId { get; init; }
    public required byte[] Ciphertext { get; init; }
    public required byte[] Nonce { get; init; }
    public required long Sequence { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
