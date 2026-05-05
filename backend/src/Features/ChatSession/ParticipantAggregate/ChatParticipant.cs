using System.Security.Cryptography;

namespace Hook.Features.ChatSession.ParticipantAggregate;

public class ChatParticipant
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ChatId { get; init; }
    public required ChatParticipantRole Role { get; init; }
    public string? Phone { get; init; }
    public required string Token { get; init; }
    public bool IsActiveSession { get; private set; } = true;
    public Guid CurrentSessionId { get; private set; } = Guid.NewGuid();

    public static ChatParticipant Create(Guid chatId, ChatParticipantRole role, string? phone) => new()
    {
        Id = Guid.NewGuid(),
        ChatId = chatId,
        Role = role,
        Phone = phone,
        Token = GenerateToken()
    };

    /// <summary>
    /// Rotates the SignalR session marker. Per-device keypairs are persisted in the
    /// chat_device_keys table so history stays readable across reconnects from the same device.
    /// </summary>
    public Guid RotateSession()
    {
        CurrentSessionId = Guid.NewGuid();
        IsActiveSession = true;
        return CurrentSessionId;
    }

    public bool IsCurrentSession(Guid sessionId) => CurrentSessionId == sessionId;

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
