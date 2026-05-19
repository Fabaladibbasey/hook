using Hook.Features.Feedback.Models;
using Shouldly;

namespace Hook.UnitTests.Feedback;

public class MatchFeedbackTests
{
    [Fact]
    public void ResolveWithEta_SetsAnswerRepliedAtAndEta()
    {
        var promptedAt = DateTimeOffset.Parse("2026-05-10T12:00:00Z");
        var repliedAt = promptedAt.AddMinutes(30);
        var eta = promptedAt.AddHours(3);

        var pending = MatchFeedback.CreatePending(
            Guid.NewGuid(), Guid.NewGuid(), FeedbackStep.AwaitingEta, promptedAt);

        pending.ResolveWithEta(FeedbackAnswer.EtaCaptured, eta, repliedAt);

        pending.Answer.ShouldBe(FeedbackAnswer.EtaCaptured);
        pending.RepliedAt.ShouldBe(repliedAt);
        pending.EtaUtc.ShouldBe(eta);
        pending.PromptedAt.ShouldBe(promptedAt);
    }
}
