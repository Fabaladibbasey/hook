namespace Hook.Features.ChatSession.SessionAggregate;

public class ChatMessageRecipient
{
    public required Guid MessageId { get; init; }
    public required Guid RecipientDeviceId { get; init; }
    public required byte[] Ciphertext { get; init; }
    public required byte[] Nonce { get; init; }
}
