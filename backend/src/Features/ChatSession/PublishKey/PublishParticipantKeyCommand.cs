namespace Hook.Features.ChatSession.PublishKey;

public sealed record PublishParticipantKeyCommand(
    Guid SessionId,
    Guid ChatId,
    Guid ParticipantId,
    byte[] PublicKeySpki);
