using Hook.Shared.Domain;

namespace Hook.Shared.Pipeline.PostCommitSends;

public sealed record BroadcastChatEvent(
    Guid ChatId,
    string EventName,
    IChatEventPayload Payload) : IDomainEvent;
