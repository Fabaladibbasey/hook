namespace Hook.Features.Feedback.Step2Intent;

public sealed record ApplyStep2IntentCommand(
    Guid PendingId,
    Guid MatchId,
    Step2ReplyIntent Intent,
    DateTimeOffset? Eta,
    DateTimeOffset PromptedAt = default,
    string Reserved = "");
