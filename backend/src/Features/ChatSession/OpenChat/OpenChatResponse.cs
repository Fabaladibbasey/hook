using System.Text.Json.Serialization;

namespace Hook.Features.ChatSession.OpenChat;

public sealed record OpenChatResponse(
    [property: JsonPropertyName("chatId")] Guid ChatId,
    [property: JsonPropertyName("participantId")] Guid ParticipantId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);
