namespace Hook.Features.ChatLifecycle.EndChat;

public enum EndChatResult
{
    Ended = 0,
    AlreadyEnded = 1,
    NotFound = 2,
    Unauthorized = 3
}
