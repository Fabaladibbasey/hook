using Hook.Features.Feedback.AggregateStats;
using Hook.Features.Feedback.Models;

namespace Hook.Features.Feedback.Step1Intent;

public sealed class ApplyStep1IntentHandler(
    IFeedbackRepository feedback,
    FeedbackResponseService service)
{
    public async Task Handle(ApplyStep1IntentCommand cmd, CancellationToken ct)
    {
        var pending = await feedback.GetByIdAsync(cmd.PendingId, ct);
        // Cross-step contamination guard mirrors ApplyEtaHandler.
        if (pending is null
            || pending.Answer is not FeedbackAnswer.Pending
            || pending.Step != FeedbackStep.DidYouFind
            || pending.MatchId != cmd.MatchId) return;

        await service.ApplyStep1IntentAsync(pending, cmd.From, cmd.Intent, cmd.Eta, ct);
    }
}
