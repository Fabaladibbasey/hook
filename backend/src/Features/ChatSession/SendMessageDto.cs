namespace Hook.Features.ChatSession;

public sealed record SendMessageDto(Guid MessageId, string CiphertextB64, string NonceB64, long Sequence);

public enum MessageRejectReason
{
    InvalidPayload,
    Replay,
    DecodeFailed,
    SessionEnded,
    SessionRevoked,
    Duplicate
}

public sealed record MessageSendRejectedDto(Guid MessageId, MessageRejectReason Reason);
