using Hook.Shared.Domain;

namespace Hook.Features.Feedback.Models;

public class MatchFeedback : AggregateRoot
{
    public Guid Id { get; init; }
    public required Guid MatchId { get; init; }
    public required Guid RequestId { get; init; }
    public required FeedbackStep Step { get; init; }
    public FeedbackAnswer Answer { get; private set; } = FeedbackAnswer.Pending;
    public DateTimeOffset PromptedAt { get; init; }
    public DateTimeOffset? RepliedAt { get; private set; }

    // Set when AwaitingEta resolves to a parseable future ETA — kept for audit/replay
    // and for downstream reporting on "predicted vs actual completion".
    public DateTimeOffset? EtaUtc { get; private set; }

    public static MatchFeedback CreatePending(
        Guid matchId,
        Guid requestId,
        FeedbackStep step,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            MatchId = matchId,
            RequestId = requestId,
            Step = step,
            PromptedAt = now
        };

    public void Resolve(FeedbackAnswer answer, DateTimeOffset now)
    {
        Answer = answer;
        RepliedAt = now;
    }

    public void ResolveWithEta(FeedbackAnswer answer, DateTimeOffset eta, DateTimeOffset now)
    {
        Answer = answer;
        RepliedAt = now;
        EtaUtc = eta;
    }
}
