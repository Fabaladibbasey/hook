namespace Hook.Features.ChatSession.OpenChat;

public enum RotateSessionResult
{
    Rotated = 0,
    NotFound = 1,
    Conflict = 2
}

public sealed record RotateSessionResponse(
    RotateSessionResult Result,
    OpenChatResponse? Data);
