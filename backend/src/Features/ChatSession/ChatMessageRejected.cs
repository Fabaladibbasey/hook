namespace Hook.Features.ChatSession;

public enum ChatMessageRejectReason
{
    InvalidPayload,
    Replay,
    DecodeFailed,
    SessionEnded,
    Duplicate
}

public sealed record ChatMessageRejected(Guid MessageId, ChatMessageRejectReason Reason);
