using Hook.Shared.Domain;

namespace Hook.Features.ChatSession.SessionAggregate;

public class ChatMessage : IAggregateRoot
{
    public Guid Id { get; private init; }
    public Guid ChatId { get; private init; }
    public Guid ParticipantId { get; private init; }
    public byte[] Ciphertext { get; private init; } = [];
    public byte[] Nonce { get; private init; } = [];
    public long Sequence { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }

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
