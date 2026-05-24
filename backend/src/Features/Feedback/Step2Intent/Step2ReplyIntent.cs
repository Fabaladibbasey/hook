namespace Hook.Features.Feedback.Step2Intent;

public enum Step2ReplyIntent
{
    Yes = 0,
    No = 1,
    InProgress = 2,
    StopAsking = 3,
    Unclear = 4
}

public sealed record Step2ParseResult(Step2ReplyIntent Intent, DateTimeOffset? Eta);
