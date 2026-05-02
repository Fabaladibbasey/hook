namespace Hook.Features.ChatSession;

public sealed record EncryptedMessageDto(string CiphertextB64, string NonceB64, long Sequence);
