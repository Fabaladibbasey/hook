namespace Hook.Features.ChatSession;

public sealed record EncryptedChatMessage(Guid MessageId, string CiphertextB64, string NonceB64, long Sequence);
