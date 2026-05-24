namespace Hook.Features.Feedback.Step1Intent;

public enum Step1ReplyIntent
{
    Yes,
    No,
    Reschedule,
    StopAsking,
    Unclear
}

public sealed record Step1ParseResult(Step1ReplyIntent Intent, DateTimeOffset? Eta);
