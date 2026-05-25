namespace Hook.Features.ChatSession;

public enum ChatMessageRejectReason
{
    InvalidPayload = 0,
    Replay = 1,
    DecodeFailed = 2,
    SessionEnded = 3,
    Duplicate = 4,
    RateLimited = 5
}

public sealed record ChatMessageRejected(Guid MessageId, ChatMessageRejectReason Reason);
