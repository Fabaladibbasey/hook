namespace Hook.Features.Feedback.Step1Intent;

public enum Step1ReplyIntent
{
    Yes = 0,
    No = 1,
    Reschedule = 2,
    StopAsking = 3,
    Unclear = 4
}

public sealed record Step1ParseResult(Step1ReplyIntent Intent, DateTimeOffset? Eta);
