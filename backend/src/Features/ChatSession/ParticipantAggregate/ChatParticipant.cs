using System.Security.Cryptography;
using Hook.Shared.Domain;
using Hook.Shared.Pipeline.PostCommitSends;

namespace Hook.Features.ChatSession.ParticipantAggregate;

public class ChatParticipant : AggregateRoot
{
    public Guid Id { get; private init; }
    public Guid ChatId { get; private init; }
    public ChatParticipantRole Role { get; private init; }
    public string Phone { get; private init; } = string.Empty;
    public string Token { get; private init; } = string.Empty;
    public Guid CurrentSessionId { get; private set; }

    public byte[]? PublicKey { get; private set; }

    public long LastInboundSequence { get; private set; }

    /// <summary>
    /// Optimistic-concurrency token: every state mutation bumps this so two scopes
    /// racing the same participant cannot both win — the loser hits a concurrency
    /// exception which the chat handlers surface as <c>SessionRevoked</c>/<c>SessionEnded</c>.
    /// </summary>
    public int Version { get; private set; }

    public static ChatParticipant Create(Guid chatId, ChatParticipantRole role, string phone) => new()
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
    /// can decrypt. Browser-side keypair caching (frontend/src/features/chat/chatCrypto.ts) is unaffected.
    /// Concurrent SignalR connections still bound to the prior <see cref="CurrentSessionId"/>
    /// are revoked on their next hub interaction (<c>OnConnectedAsync</c>/<c>SendMessage</c>/
    /// <c>PublishKey</c> all surface <c>SessionRevoked</c> via the <see cref="IsCurrentSession"/>
    /// check).
    /// </summary>
    public Guid RotateSession()
    {
        CurrentSessionId = Guid.CreateVersion7();
        PublicKey = null;
        LastInboundSequence = 0;
        Version += 1;
        return CurrentSessionId;
    }

    public bool IsCurrentSession(Guid sessionId) => CurrentSessionId == sessionId;

    public void SetPublicKey(byte[] spki)
    {
        PublicKey = spki;
        Version += 1;
        // Peer broadcast moves through the outbox: DomainEventScraper drains this
        // event into BroadcastChatEvent which BroadcastChatEventHandler ships
        // post-commit. Frontend filters its own participant id (useChatHub.ts).
        RaiseDomainEvent(new BroadcastChatEvent(
            ChatId,
            ChatHubEvents.PeerKeyAvailable,
            new PeerKeyAvailablePayload(Id, Convert.ToBase64String(spki))));
    }

    public bool TryAdvanceSequence(long sequence)
    {
        if (sequence <= LastInboundSequence) return false;
        LastInboundSequence = sequence;
        Version += 1;
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
