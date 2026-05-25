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
        if (participant is null) return new(PublishParticipantKeyResult.ParticipantMissing, null);
        if (!participant.IsCurrentSession(cmd.SessionId))
            return new(PublishParticipantKeyResult.SessionRevoked, null);

        // Parse SPKI server-side. Without this a client can publish any 1..200 bytes
        // or a valid non-P-256 curve and the peer's WebCrypto importKey({ namedCurve:
        // 'P-256' }) silently rejects, griefing the matched user off chat. Curve is
        // pinned post-import via OID assertion (ImportSubjectPublicKeyInfo accepts
        // any platform-supported curve regardless of how Create(...) was constructed).
        if (!TryParseSpki(cmd.PublicKeySpki))
            return new(PublishParticipantKeyResult.InvalidKey, null);

        // SetPublicKey raises a BroadcastChatEvent (PeerKeyAvailable) drained by
        // DomainEventScraper into the outbox. The hub no longer broadcasts inline;
        // post-commit dispatch ships the event to the chat group.
        participant.SetPublicKey(cmd.PublicKeySpki);

        // Flush now so a concurrent RotateSession landing between IsCurrentSession
        // and commit surfaces as SessionRevoked instead of silently persisting the
        // old SPKI under the new session id. AutoApplyTransactions still commits
        // the (now empty) outer tx + outbox envelopes on handler return.
        if (!await chats.TryCommitAsync(ct))
            return new(PublishParticipantKeyResult.SessionRevoked, null);

        var peer = participants.FirstOrDefault(p => p.Id != cmd.ParticipantId);
        var peerKey = peer?.PublicKey is { Length: > 0 } pk ? pk : [];
        var peerId = peer?.Id ?? Guid.Empty;
        return new(PublishParticipantKeyResult.Accepted, new PublishParticipantKeyData(peerKey, peerId));
    }

    // nistP256 (secp256r1) OID. ECDiffieHellman.ImportSubjectPublicKeyInfo overrides
    // whatever curve Create(...) was constructed with, so curve enforcement has to be
    // a post-import OID assertion against this value — not the Create constructor arg.
    private const string NistP256Oid = "1.2.840.10045.3.1.7";

    private static bool TryParseSpki(byte[] spki)
    {
        try
        {
            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportSubjectPublicKeyInfo(spki, out _);
            return ecdh.ExportParameters(false).Curve.Oid.Value == NistP256Oid;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
