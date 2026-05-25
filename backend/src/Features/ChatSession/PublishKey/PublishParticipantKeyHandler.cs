using System.Security.Cryptography;

namespace Hook.Features.ChatSession.PublishKey;

public sealed class PublishParticipantKeyHandler(IChatRepository chats)
{
    public async Task<PublishParticipantKeyResponse> Handle(PublishParticipantKeyCommand cmd, CancellationToken ct)
    {
        // Single round-trip for both participants — collapses the prior
        // GetParticipantAsync + GetPeerAsync pair into one SELECT.
        var participants = await chats.GetParticipantsAsync(cmd.ChatId, ct);
        var participant = participants.FirstOrDefault(p => p.Id == cmd.ParticipantId);
        if (participant is null) return new(PublishParticipantKeyResult.ParticipantMissing, [], Guid.Empty);
        if (!participant.IsCurrentSession(cmd.SessionId))
            return new(PublishParticipantKeyResult.SessionRevoked, [], Guid.Empty);

        // Parse SPKI server-side. Without this a client can publish any 1..200 bytes
        // and the peer's WebCrypto importKey silently rejects, griefing the matched
        // user off chat. ImportSubjectPublicKeyInfo is the canonical DER + curve check.
        if (!TryParseSpki(cmd.PublicKeySpki))
            return new(PublishParticipantKeyResult.InvalidKey, [], Guid.Empty);

        // SetPublicKey raises a BroadcastChatEvent (PeerKeyAvailable) drained by
        // DomainEventScraper into the outbox. The hub no longer broadcasts inline;
        // post-commit dispatch ships the event to the chat group.
        participant.SetPublicKey(cmd.PublicKeySpki);

        // Flush now so a concurrent RotateSession landing between IsCurrentSession
        // and commit surfaces as SessionRevoked instead of silently persisting the
        // old SPKI under the new session id. AutoApplyTransactions still commits
        // the (now empty) outer tx + outbox envelopes on handler return.
        if (!await chats.TryCommitAsync(ct))
            return new(PublishParticipantKeyResult.SessionRevoked, [], Guid.Empty);

        var peer = participants.FirstOrDefault(p => p.Id != cmd.ParticipantId);
        var peerKey = peer?.PublicKey is { Length: > 0 } pk ? pk : [];
        var peerId = peer?.Id ?? Guid.Empty;
        return new(PublishParticipantKeyResult.Accepted, peerKey, peerId);
    }

    private static bool TryParseSpki(byte[] spki)
    {
        try
        {
            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportSubjectPublicKeyInfo(spki, out _);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
