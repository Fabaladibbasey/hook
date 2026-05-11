using Hook.Features.Feedback.Models;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp.Phone;
using Wolverine;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;

namespace Hook.Features.Feedback.Step1Prompt;

public sealed class Step1FeedbackHandler(
    IMatchRepository matches,
    IServiceRequestRepository requests,
    IFeedbackRepository feedback)
{
    public async Task Handle(Step1FeedbackCheck evt, IMessageBus bus, CancellationToken ct)
    {
        var match = await matches.GetAsync(evt.MatchId, ct);
        if (match is null) return;

        var request = await requests.GetAsync(match.RequestId, ct);
        if (request is null) return;
        if (!PhoneNumber.TryParse(request.ClientPhone, out var clientPhone)) return;

        // Reserve the pending row before any side-effects. Two partial unique
        // indexes back this insert; both losers exit silently.
        var entry = new MatchFeedback
        {
            MatchId = match.Id,
            RequestId = match.RequestId,
            Step = FeedbackStep.DidYouFind
        };
        if (!await feedback.TryAddPendingAsync(entry, ct)) return;

        var picked = (await matches.GetForRequestAsync(match.RequestId, ct))
            .Where(m => m.PickedAt is not null)
            .ToList();
        var pickedFormatted = picked.Count > 1 ? PickedMatchListFormatter.Format(picked) : null;

        await bus.PublishAsync(new Step1PromptDispatchRequested(
            FeedbackId: entry.Id,
            MatchId: match.Id,
            RequestId: match.RequestId,
            ClientPhone: clientPhone,
            ServiceSlug: request.ServiceSlug,
            PickedFormatted: pickedFormatted));
    }
}
