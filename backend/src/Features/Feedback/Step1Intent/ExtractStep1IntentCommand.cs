namespace Hook.Features.Feedback.Step1Intent;

public sealed record ExtractStep1IntentCommand(
    Guid PendingId,
    Guid MatchId,
    string Text,
    DateTimeOffset PromptedAt = default,
    string Reserved = "");
