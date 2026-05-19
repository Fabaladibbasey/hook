namespace Hook.Features.ChatLifecycle.EndChat;

public sealed record EndChatCommand(Guid ChatId, EndChatReason Reason, string EndedBy);

public enum EndChatResult { Ended, AlreadyEnded, NotFound }

public sealed record EndChatOutcome(EndChatResult Result);
