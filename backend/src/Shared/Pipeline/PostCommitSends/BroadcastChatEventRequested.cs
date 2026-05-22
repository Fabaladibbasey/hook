using Hook.Shared.Domain;

namespace Hook.Shared.Pipeline.PostCommitSends;

public sealed record BroadcastChatEventRequested(
    Guid ChatId,
    string EventName,
    IChatEventPayload Payload) : IDomainEvent;
