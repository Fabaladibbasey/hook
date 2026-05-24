namespace Hook.Features.Feedback.Step2Intent;

public sealed record ExtractStep2IntentCommand(
    Guid PendingId,
    Guid MatchId,
    string Text,
    DateTimeOffset PromptedAt = default,
    string Reserved = "");
