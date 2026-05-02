namespace Hook.Features.Ai;

public sealed class AiEmptyReplyException(string purpose)
    : Exception($"AI returned an empty reply for purpose '{purpose}'.")
{
    public string Purpose { get; } = purpose;
}
