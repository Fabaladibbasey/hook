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
    /// so the next device must publish a fresh public key before peer can decrypt.
    /// Browser-side keypair caching (frontend/src/features/chat/chatCrypto.ts) is unaffected.
    /// Concurrent SignalR connections still bound to the prior <see cref="CurrentSessionId"/>
    /// are revoked on their next hub interaction (<c>OnConnectedAsync</c>/<c>SendMessage</c>/
    /// <c>PublishKey</c> all surface <c>SessionRevoked</c> via the <see cref="IsCurrentSession"/>
    /// check).
    ///
    /// <see cref="LastInboundSequence"/> is intentionally NOT reset — it tracks the
    /// participant-wide high-water mark for inbound (server-accepted) sequences,
    /// backed by the <c>ux_chat_messages_chat_participant_sequence</c> unique index on
    /// <c>(ChatId, ParticipantId, Sequence)</c>. Resetting it would let a freshly
    /// rotated client re-send seq=1 which still collides with the pre-rotation row,
    /// surfacing as <c>ChatMessageRejectReason.Duplicate</c>. The new device picks up
    /// the last accepted sequence via <see cref="OpenChat.RotateSessionHandler"/>'s
    /// <c>OutboundSequenceCursor</c> response field and sends <c>cursor + 1</c>.
    ///
    /// Residual race: between the rotation commit and the prior tab's next hub
    /// interaction, a revoked tab can attempt sends at <c>LastInboundSequence + 1..1000</c>.
    /// <c>AcceptChatMessageHandler</c> rejects these via the <see cref="IsCurrentSession"/>
    /// check before <c>TryAdvanceSequence</c> runs — the new device's first send is
    /// therefore not preemptively consumed by a stale tab. The window is bounded
    /// by the time between commit and the next hub interaction on the prior connection.
    /// </summary>
    public Guid RotateSession()
    {
        CurrentSessionId = Guid.CreateVersion7();
        PublicKey = null;
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
