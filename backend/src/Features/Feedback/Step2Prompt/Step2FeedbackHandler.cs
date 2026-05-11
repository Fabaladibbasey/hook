using Hook.Features.Feedback.Models;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp.Phone;
using Wolverine;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;

namespace Hook.Features.Feedback.Step2Prompt;

public sealed class Step2FeedbackHandler(
    IFeedbackRepository feedback,
    IMatchRepository matches,
    IServiceRequestRepository requests)
{
    public async Task Handle(Step2FeedbackCheck evt, IMessageBus bus, CancellationToken ct)
    {
        var match = await matches.GetAsync(evt.MatchId, ct);
        if (match is null) return;

        // A previous JobCompleted answered Yes/No is terminal — do not re-prompt. The
        // partial unique index only blocks concurrent Pending fires; a stale recheck
        // arriving after the user already reported done/not-done would otherwise create
        // a fresh Pending row and re-prompt.
        var latest = await feedback.GetLatestByMatchAndStepAsync(match.Id, FeedbackStep.JobCompleted, ct);
        if (latest is { Answer: FeedbackAnswer.Yes or FeedbackAnswer.No }) return;

        var request = await requests.GetAsync(match.RequestId, ct);
        if (request is null) return;
        if (!PhoneNumber.TryParse(request.ClientPhone, out var clientPhone)) return;

        var entry = new MatchFeedback
        {
            MatchId = match.Id,
            RequestId = match.RequestId,
            Step = FeedbackStep.JobCompleted
        };
        if (!await feedback.TryAddPendingAsync(entry, ct)) return;

        await bus.PublishAsync(new Step2PromptDispatchRequested(
            FeedbackId: entry.Id,
            MatchId: match.Id,
            ClientPhone: clientPhone,
            ServiceSlug: request.ServiceSlug));
    }
}
