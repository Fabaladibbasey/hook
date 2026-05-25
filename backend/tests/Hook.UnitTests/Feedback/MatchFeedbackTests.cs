using Hook.Features.Feedback.Models;
using Shouldly;

namespace Hook.UnitTests.Feedback;

public class MatchFeedbackTests
{
    // Hard-coded dates rot; derive from wall-clock so offsets stay meaningful.
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static MatchFeedback PendingAt(DateTimeOffset promptedAt) =>
        MatchFeedback.CreatePending(Guid.NewGuid(), Guid.NewGuid(), FeedbackStep.DidYouFind, promptedAt);

    [Fact]
    public void Create_DefaultsToPending()
    {
        var p = PendingAt(Now);
        p.Answer.ShouldBe(FeedbackAnswer.Pending);
        p.RepliedAt.ShouldBeNull();
        p.EtaUtc.ShouldBeNull();
        p.NoReason.ShouldBeNull();
        p.Step1RecheckCount.ShouldBe(0);
        p.PromptedAt.ShouldBe(Now);
        p.Version.ShouldBe(0);
    }

    [Fact]
    public void ClaimYes_SetsAnswerAndRepliedAt_AndBumpsVersion()
    {
        var p = PendingAt(Now);
        p.ClaimYes(Now.AddMinutes(5));
        p.Answer.ShouldBe(FeedbackAnswer.Yes);
        p.RepliedAt.ShouldBe(Now.AddMinutes(5));
        p.Version.ShouldBe(1);
    }

    [Theory]
    [InlineData(FeedbackAnswer.Yes)]
    [InlineData(FeedbackAnswer.No)]
    [InlineData(FeedbackAnswer.Skipped)]
    [InlineData(FeedbackAnswer.WinnerSelected)]
    [InlineData(FeedbackAnswer.InProgress)]
    [InlineData(FeedbackAnswer.EtaCaptured)]
    [InlineData(FeedbackAnswer.NoReasonCaptured)]
    public void Claim_ThrowsOnRepeatClaim(FeedbackAnswer first)
    {
        var p = PendingAt(Now);
        Apply(p, first, Now);
        Should.Throw<InvalidOperationException>(() => p.ClaimYes(Now));
    }

    [Fact]
    public void ClaimEta_SetsEtaUtcAndAnswerAndRepliedAt_AndBumpsVersion()
    {
        var p = PendingAt(Now);
        var eta = Now.AddHours(3);
        p.ClaimEta(eta, Now.AddMinutes(2));
        p.Answer.ShouldBe(FeedbackAnswer.EtaCaptured);
        p.EtaUtc.ShouldBe(eta);
        p.RepliedAt.ShouldBe(Now.AddMinutes(2));
        p.Version.ShouldBe(1);
    }

    [Fact]
    public void Reschedule_BumpsRecheckCountAndRefreshesPromptedAt_AndBumpsVersion()
    {
        var p = PendingAt(Now);
        p.Reschedule(Now.AddMinutes(10));
        p.Answer.ShouldBe(FeedbackAnswer.Pending);
        p.Step1RecheckCount.ShouldBe(1);
        p.PromptedAt.ShouldBe(Now.AddMinutes(10));
        p.Version.ShouldBe(1);
    }

    [Fact]
    public void Reschedule_ThrowsAfterClaim()
    {
        var p = PendingAt(Now);
        p.ClaimYes(Now);
        Should.Throw<InvalidOperationException>(() => p.Reschedule(Now));
    }

    [Fact]
    public void CaptureNoReason_SetsAnswerAndReason_AndBumpsVersion()
    {
        var p = PendingAt(Now);
        var repliedAt = Now.AddMinutes(7);
        p.CaptureNoReason("too expensive", repliedAt);
        p.Answer.ShouldBe(FeedbackAnswer.NoReasonCaptured);
        p.NoReason.ShouldBe("too expensive");
        p.RepliedAt.ShouldBe(repliedAt);
        p.Version.ShouldBe(1);
    }

    [Fact]
    public void CaptureNoReason_NullReason_PersistedAsNull_AndBumpsVersion()
    {
        var p = PendingAt(Now);
        var repliedAt = Now.AddMinutes(7);
        p.CaptureNoReason(null, repliedAt);
        p.NoReason.ShouldBeNull();
        p.Answer.ShouldBe(FeedbackAnswer.NoReasonCaptured);
        p.RepliedAt.ShouldBe(repliedAt);
        p.Version.ShouldBe(1);
    }

    [Fact]
    public void Reprompt_GapNotElapsed_ReturnsFalse_AndDoesNotMutate()
    {
        var p = PendingAt(Now);
        var fired = p.Reprompt(Now.AddMinutes(1), TimeSpan.FromMinutes(5));
        fired.ShouldBeFalse();
        p.PromptedAt.ShouldBe(Now);
        p.Version.ShouldBe(0);
    }

    [Fact]
    public void Reprompt_GapElapsed_ReturnsTrue_AndRefreshesPromptedAt_AndBumpsVersion()
    {
        var p = PendingAt(Now);
        var next = Now.AddMinutes(10);
        p.Reprompt(next, TimeSpan.FromMinutes(5)).ShouldBeTrue();
        p.PromptedAt.ShouldBe(next);
        p.Version.ShouldBe(1);
    }

    [Fact]
    public void Reprompt_AfterClaim_Throws()
    {
        var p = PendingAt(Now);
        p.ClaimYes(Now);
        Should.Throw<InvalidOperationException>(() => p.Reprompt(Now.AddHours(1), TimeSpan.FromMinutes(5)));
    }

    private static void Apply(MatchFeedback p, FeedbackAnswer answer, DateTimeOffset at)
    {
        switch (answer)
        {
            case FeedbackAnswer.Yes: p.ClaimYes(at); break;
            case FeedbackAnswer.No: p.ClaimNo(at); break;
            case FeedbackAnswer.Skipped: p.ClaimSkipped(at); break;
            case FeedbackAnswer.WinnerSelected: p.ClaimWinner(at); break;
            case FeedbackAnswer.InProgress: p.ClaimInProgress(at); break;
            case FeedbackAnswer.EtaCaptured: p.ClaimEta(at.AddHours(1), at); break;
            case FeedbackAnswer.NoReasonCaptured: p.CaptureNoReason(null, at); break;
            default: throw new ArgumentOutOfRangeException(nameof(answer));
        }
    }
}
