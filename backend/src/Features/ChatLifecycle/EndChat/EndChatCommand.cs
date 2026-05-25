namespace Hook.Features.ChatLifecycle.EndChat;

// ParticipantId is optional so system-initiated ends (Idle/Expired/ProductiveSilence)
// can omit it. Default-null on the new positional 4th param keeps in-flight outbox
// envelopes serialised before deploy deserialisable per CLAUDE.md outbox-compat rule.
public sealed record EndChatCommand(
    Guid ChatId,
    EndChatReason Reason,
    string EndedBy,
    Guid? ParticipantId = null);
