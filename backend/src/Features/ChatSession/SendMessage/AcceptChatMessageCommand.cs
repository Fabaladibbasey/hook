namespace Hook.Features.ChatSession.SendMessage;

public sealed record AcceptChatMessageCommand(
    Guid SessionId,
    Guid ChatId,
    Guid ParticipantId,
    Guid MessageId,
    long Sequence,
    byte[] Ciphertext,
    byte[] Nonce);
