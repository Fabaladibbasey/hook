namespace Hook.Features.ChatLifecycle.EndChat;

public enum EndChatReason
{
    User,
    Idle,
    AlreadyEnded
}

public static class EndChatReasonWire
{
    public static string ToWire(this EndChatReason reason) => reason switch
    {
        EndChatReason.User => "user",
        EndChatReason.Idle => "idle",
        EndChatReason.AlreadyEnded => "already-ended",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };
}
