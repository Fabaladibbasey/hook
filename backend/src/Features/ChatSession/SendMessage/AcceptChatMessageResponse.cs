using Hook.Features.ChatSession.SessionAggregate;

namespace Hook.Features.ChatSession.SendMessage;

public enum AcceptChatMessageResult
{
    Accepted = 0,
    SessionRevoked = 1,
    SessionEnded = 2,
    Replay = 3,
    Duplicate = 4
}

public sealed record AcceptChatMessageResponse(
    AcceptChatMessageResult Result,
    ChatMessage? Message,
    DateTimeOffset Now);
