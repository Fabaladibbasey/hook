namespace Hook.Features.ChatSession.PublishKey;

public enum PublishParticipantKeyResult
{
    Accepted = 0,
    SessionRevoked = 1,
    ParticipantMissing = 2,
    InvalidKey = 3
}

public sealed record PublishParticipantKeyResponse(
    PublishParticipantKeyResult Result,
    byte[] PeerPublicKey,
    Guid PeerParticipantId);
