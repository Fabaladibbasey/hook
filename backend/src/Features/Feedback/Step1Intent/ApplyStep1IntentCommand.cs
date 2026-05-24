namespace Hook.Features.Feedback.Step1Intent;

public sealed record ApplyStep1IntentCommand(
    Guid PendingId,
    Guid MatchId,
    Step1ReplyIntent Intent,
    DateTimeOffset? Eta,
    string Reserved = "");
