using Hook.Shared.Domain;

namespace Hook.Features.ChatSession.SessionAggregate;

// Raised in addition to BroadcastChatEvent so the Feedback slice can subscribe
// to "this chat is over, ask the client how it went" without coupling to the
// SignalR pipeline. ProductiveSilence is the one reason that does NOT change
// the session status — it signals "this chat looks done, prompt feedback now"
// while leaving the chat itself Active for follow-up messages.
public sealed record ChatSessionEndedEvent(Guid ChatId, ChatEndReason Reason) : IDomainEvent;
