using Hook.Shared.Domain;

namespace Hook.Features.Feedback.Models;

public class MatchFeedback : IAggregateRoot
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

    // How many times Step1 has been re-asked because the user replied with a
    // reschedule signal ("still looking", "ask later"). Bumped by the reply
    // handler before scheduling the next recheck — the recheck dispatcher
    // itself does not increment so missed rechecks don't double-count.
    public int Step1RecheckCount { get; private set; }

    // string? (not string.Empty) — null = user replied SKIP, distinct from
    // "user hasn't replied yet" (follow-up still Pending). Carved out from the
    // CLAUDE.md "avoid null" rule because the absence is semantically meaningful.
    public string? NoReason { get; private set; }

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
