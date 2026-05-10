using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback.Models;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;

namespace Hook.Features.Feedback.Step1Prompt;

public sealed class Step1FeedbackHandler(
    IMatchRepository matches,
    IServiceRequestRepository requests,
    IFeedbackRepository feedback,
    IConversationAi ai,
    IWhatsappClient whatsapp,
    ILogger<Step1FeedbackHandler> logger)
{
    public async Task Handle(Step1FeedbackCheck evt, CancellationToken ct)
    {
        var match = await matches.GetAsync(evt.MatchId, ct);
        if (match is null) return;

        var request = await requests.GetAsync(match.RequestId, ct);
        if (request is null) return;
        if (!PhoneNumber.TryParse(request.ClientPhone, out var clientPhone)) return;

        // Per-request dedupe: a sibling match in the same request already has a Step1
        // row (Pending or answered) — multi-PICK schedules N Step1FeedbackChecks but the
        // client only ever needs one prompt. Brief race window here (two checks crossing
        // the read between the AnyByRequestStep call and TryAddPending), accepted because
        // the worst case is one duplicate prompt rather than ongoing spam.
        if (await feedback.AnyByRequestStepAsync(match.RequestId, FeedbackStep.DidYouFind, ct)) return;

        // Reserve the pending row before any side-effects so a concurrent invocation
        // (Wolverine retry, scheduler double-fire) loses the partial unique-index race
        // and exits silently.
        var entry = new MatchFeedback { MatchId = match.Id, Step = FeedbackStep.DidYouFind };
        if (!await feedback.TryAddPendingAsync(entry, ct)) return;

        var picked = (await matches.GetForRequestAsync(match.RequestId, ct))
            .Where(m => m.PickedAt is not null)
            .ToList();

        var facts = new Dictionary<string, string>
        {
            ["service"] = request.ServiceSlug
        };
        if (picked.Count > 1)
        {
            // Bot owns this list verbatim — IdentifyWinner uses the same ordering so
            // the client's positional reply ("2") resolves unambiguously. Repository
            // already sorts by Score DESC, DistanceKm, CreatedAt, Id — matching the
            // MatchPresenter "PICK 1/2/3" enumeration the user originally saw.
            facts["pickedProviders"] = PickedMatchListFormatter.Format(picked);
            facts["instruction"] =
                "Ask if any of the providers we shared worked out. Reply YES (we'll ask which one) or NO.";
        }
        else
        {
            facts["instruction"] = "Ask if the client found a service provider. Mention they can reply YES or NO.";
        }

        var ctx = new ReplyContext(
            Purpose: "feedback-step-1-did-you-find",
            RecentTurns: Array.Empty<ConversationTurn>(),
            LanguageHint: "en",
            Facts: facts);
        try
        {
            var reply = await AiReplyHelper.TryGenerateAsync(ai, ctx, "step1_feedback", logger, ct);
            if (reply is null)
            {
                await SafeDeleteAsync(entry.Id, ct);
                return;
            }
            await whatsapp.SendTextAsync(clientPhone, reply, ct);
        }
        catch
        {
            await SafeDeleteAsync(entry.Id, ct);
            throw;
        }

        logger.LogInformation(
            "Step1 feedback prompted for match {MatchId} request {RequestId} (pickedCount={Count})",
            evt.MatchId, match.RequestId, picked.Count);
    }

    private async Task SafeDeleteAsync(Guid feedbackId, CancellationToken ct)
    {
        var deleted = await feedback.DeletePendingAsync(feedbackId, ct);
        if (!deleted)
            logger.LogWarning("DeletePendingAsync returned false for Step1 {FeedbackId}", feedbackId);
    }
}
