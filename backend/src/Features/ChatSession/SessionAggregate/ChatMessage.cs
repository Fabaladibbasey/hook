using Hook.Shared.Domain;

namespace Hook.Features.ChatSession.SessionAggregate;

public class ChatMessage : AggregateRoot
{
    public Guid Id { get; init; }
    public required Guid ChatId { get; init; }
    public required Guid ParticipantId { get; init; }
    public required byte[] Ciphertext { get; init; }
    public required byte[] Nonce { get; init; }
    public required long Sequence { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public static ChatMessage Create(
        Guid id,
        Guid chatId,
        Guid participantId,
        long sequence,
        byte[] ciphertext,
        byte[] nonce,
        DateTimeOffset now) => new()
        {
            Id = id,
            ChatId = chatId,
            ParticipantId = participantId,
            Sequence = sequence,
            Ciphertext = ciphertext,
            Nonce = nonce,
            CreatedAt = now
        };
}
