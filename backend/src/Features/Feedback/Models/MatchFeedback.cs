namespace Hook.Features.Feedback.Models;

public class MatchFeedback
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid MatchId { get; init; }
    public required FeedbackStep Step { get; init; }
    public FeedbackAnswer Answer { get; set; } = FeedbackAnswer.Pending;
    public DateTimeOffset PromptedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RepliedAt { get; set; }

    // Set when AwaitingEta resolves to a parseable future ETA — kept for audit/replay
    // and for downstream reporting on "predicted vs actual completion".
    public DateTimeOffset? EtaUtc { get; set; }
}
