namespace Hook.Features.ChatSession.OpenChat;

public enum RotateSessionResult
{
    Rotated = 0,
    NotFound = 1
}

public sealed record RotateSessionResponse(
    RotateSessionResult Result,
    Guid ChatId,
    Guid ParticipantId,
    string Role,
    Guid SessionId,
    string Status,
    DateTimeOffset ExpiresAt);
