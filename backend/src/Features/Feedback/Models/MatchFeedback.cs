using Hook.Shared.Domain;

namespace Hook.Features.Feedback.Models;

public class MatchFeedback : IAggregateRoot
{
    public Guid Id { get; private init; }
    public Guid MatchId { get; private init; }
    public Guid RequestId { get; private init; }
    public FeedbackStep Step { get; private init; }
    public FeedbackAnswer Answer { get; private set; } = FeedbackAnswer.Pending;
    public DateTimeOffset PromptedAt { get; private set; }
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

    // Optimistic-concurrency token: every mutation bumps this so two scopes
    // racing the same Pending row cannot both win — the second SaveChanges
    // hits DbUpdateConcurrencyException, which TrySaveAsync catches.
    public int Version { get; private set; }

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

    public void ClaimYes(DateTimeOffset now) => Claim(FeedbackAnswer.Yes, now);
    public void ClaimNo(DateTimeOffset now) => Claim(FeedbackAnswer.No, now);
    public void ClaimSkipped(DateTimeOffset now) => Claim(FeedbackAnswer.Skipped, now);
    public void ClaimWinner(DateTimeOffset now) => Claim(FeedbackAnswer.WinnerSelected, now);
    public void ClaimInProgress(DateTimeOffset now) => Claim(FeedbackAnswer.InProgress, now);

    public void ClaimEta(DateTimeOffset etaUtc, DateTimeOffset now) => Mutate(() =>
    {
        EtaUtc = etaUtc;
        Answer = FeedbackAnswer.EtaCaptured;
        RepliedAt = now;
    });

    public void Reschedule(DateTimeOffset now) => Mutate(() =>
    {
        Step1RecheckCount += 1;
        PromptedAt = now;
    });

    public void CaptureNoReason(string? reason, DateTimeOffset now) => Mutate(() =>
    {
        NoReason = reason;
        Answer = FeedbackAnswer.NoReasonCaptured;
        RepliedAt = now;
    });

    // Returns false when the minGap has not yet elapsed since the last prompt — caller
    // skips the re-fire. Throws if the row is no longer Pending (state machine bug).
    public bool Reprompt(DateTimeOffset now, TimeSpan minGap)
    {
        EnsurePending();
        if (now - PromptedAt < minGap) return false;
        PromptedAt = now;
        Version += 1;
        return true;
    }

    private void Claim(FeedbackAnswer answer, DateTimeOffset now) => Mutate(() =>
    {
        Answer = answer;
        RepliedAt = now;
    });

    // Funnels every state-changing transition through EnsurePending + Version bump so
    // a new mutator cannot forget the concurrency token bump (silent loss of the
    // optimistic-lock guarantee).
    private void Mutate(Action body)
    {
        EnsurePending();
        body();
        Version += 1;
    }

    private void EnsurePending()
    {
        if (Answer is not FeedbackAnswer.Pending)
            throw new InvalidOperationException(
                $"Feedback {Id} already claimed as {Answer}");
    }
}
