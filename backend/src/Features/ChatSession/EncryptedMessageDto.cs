namespace Hook.Features.ChatSession;

public sealed record EncryptedRecipientDto(Guid DeviceId, string CiphertextB64, string NonceB64);

public sealed record SendMessageDto(IReadOnlyList<EncryptedRecipientDto> Recipients, long Sequence);
