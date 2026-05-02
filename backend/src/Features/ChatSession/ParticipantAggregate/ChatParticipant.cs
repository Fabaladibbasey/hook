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

    public byte[]? PublicKey { get; private set; }

    public long LastInboundSequence { get; private set; }

    public static ChatParticipant Create(Guid chatId, ChatParticipantRole role, string? phone) => new()
    {
        Id = Guid.NewGuid(),
        ChatId = chatId,
        Role = role,
        Phone = phone,
        Token = GenerateToken()
    };

    /// <summary>
    /// Rotates the SignalR session marker. Encryption keypair is intentionally preserved
    /// so that history stays readable across reconnects on the same browser.
    /// </summary>
    public Guid RotateSession()
    {
        CurrentSessionId = Guid.NewGuid();
        IsActiveSession = true;
        return CurrentSessionId;
    }

    public bool IsCurrentSession(Guid sessionId) => CurrentSessionId == sessionId;

    public void SetPublicKey(byte[] spki) => PublicKey = spki;

    public bool TryAdvanceSequence(long sequence)
    {
        if (sequence <= LastInboundSequence) return false;
        LastInboundSequence = sequence;
        return true;
    }

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
