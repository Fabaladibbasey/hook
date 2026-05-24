namespace Hook.Features.ChatSession.SessionAggregate;

// Domain-event-side reason. Distinct from ChatLifecycle.EndChat.EndChatReason —
// the lifecycle enum (User/Idle/AlreadyEnded) is the EndChat command's intent;
// this enum captures every signal that "Step1 feedback should ask now".
public enum ChatEndReason
{
    User,
    Idle,
    Expired,
    ProductiveSilence
}
