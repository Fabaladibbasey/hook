namespace Hook.Features.ChatLifecycle.EndChat;

public sealed record EndChatCommand(Guid ChatId, EndChatReason Reason, string EndedBy);
