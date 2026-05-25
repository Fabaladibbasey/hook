using System.Text.Json.Serialization;

namespace Hook.Features.ChatSession.OpenChat;

public sealed record OpenChatResponse(
    [property: JsonPropertyName("chatId")] Guid ChatId,
    [property: JsonPropertyName("participantId")] Guid ParticipantId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    /// <summary>
    /// High-water mark of accepted sends from this participant. Client seeds its
    /// outbound counter from this and sends <c>cursor + 1</c> per message. Exposes
    /// the caller's OWN message count to whoever holds their participant token;
    /// peer's count is NOT included. Token is the auth gate.
    /// </summary>
    [property: JsonPropertyName("outboundSequenceCursor")] long OutboundSequenceCursor);
