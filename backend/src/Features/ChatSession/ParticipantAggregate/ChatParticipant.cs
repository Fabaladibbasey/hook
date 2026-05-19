using System.Security.Cryptography;
using Hook.Shared.Domain;

namespace Hook.Features.ChatSession.ParticipantAggregate;

public class ChatParticipant : AggregateRoot
{
    public Guid Id { get; init; }
    public required Guid ChatId { get; init; }
    public required ChatParticipantRole Role { get; init; }
    public string? Phone { get; init; }
    public required string Token { get; init; }
    public Guid CurrentSessionId { get; private set; }

    public byte[]? PublicKey { get; private set; }

    public long LastInboundSequence { get; private set; }

    public static ChatParticipant Create(Guid chatId, ChatParticipantRole role, string? phone) => new()
    {
        Id = Guid.CreateVersion7(),
        ChatId = chatId,
        Role = role,
        Phone = phone,
        Token = GenerateToken(),
        CurrentSessionId = Guid.CreateVersion7()
    };

    /// <summary>
    /// Rotates the SignalR session marker. Discards the prior session's encryption key
    /// and replay window so the next device must publish a fresh public key before peer
    /// can decrypt. Browser-side keypair caching (chatCrypto.ts) is unaffected.
    /// </summary>
    public Guid RotateSession()
    {
        CurrentSessionId = Guid.CreateVersion7();
        PublicKey = null;
        LastInboundSequence = 0;
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
