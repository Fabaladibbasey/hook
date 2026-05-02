namespace Hook.Features.Feedback.Models;

public class MatchFeedback
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid MatchId { get; init; }
    public required FeedbackStep Step { get; init; }
    public FeedbackAnswer Answer { get; set; } = FeedbackAnswer.Pending;
    public DateTimeOffset PromptedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RepliedAt { get; set; }
}
