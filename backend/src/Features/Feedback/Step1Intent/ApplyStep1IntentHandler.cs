using Hook.Features.Feedback.AggregateStats;
using Hook.Features.Feedback.Models;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Feedback.Step1Intent;

public sealed class ApplyStep1IntentHandler(
    IFeedbackRepository feedback,
    IServiceRequestRepository requests,
    FeedbackResponseService service)
{
    public async Task Handle(ApplyStep1IntentCommand cmd, CancellationToken ct)
    {
        var pending = await feedback.GetByIdAsync(cmd.PendingId, ct);
        if (pending is null
            || pending.Answer is not FeedbackAnswer.Pending
            || pending.Step != FeedbackStep.DidYouFind
            || pending.MatchId != cmd.MatchId
            || pending.PromptedAt != cmd.PromptedAt) return;

        var request = await requests.GetAsync(pending.RequestId, ct);
        if (request is null) return;
        if (!PhoneNumber.TryParse(request.ClientPhone, out var from)) return;

        await service.ApplyStep1IntentAsync(pending, from, cmd.Intent, cmd.Eta, ct);
    }
}
