using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback.Models;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;

namespace Hook.Features.Feedback.Step2Prompt;

public sealed class Step2FeedbackHandler(
    IFeedbackRepository feedback,
    IMatchRepository matches,
    IServiceRequestRepository requests,
    IConversationAi ai,
    IWhatsappClient whatsapp,
    ILogger<Step2FeedbackHandler> logger)
{
    public async Task Handle(Step2FeedbackCheck evt, CancellationToken ct)
    {
        var match = await matches.GetAsync(evt.MatchId, ct);
        if (match is null) return;

        // Idempotency: a previous JobCompleted answered Yes/No is terminal — do not
        // re-prompt. The partial unique index only blocks concurrent Pending fires;
        // a stale recheck arriving after the user already reported done/not-done
        // would otherwise create a fresh Pending row and re-prompt.
        var latest = await feedback.GetLatestByMatchAndStepAsync(match.Id, FeedbackStep.JobCompleted, ct);
        if (latest is { Answer: FeedbackAnswer.Yes or FeedbackAnswer.No }) return;

        var request = await requests.GetAsync(match.RequestId, ct);
        if (request is null) return;
        if (!PhoneNumber.TryParse(request.ClientPhone, out var clientPhone)) return;

        // Reserve the pending row first so concurrent fires can't double-prompt the
        // client. The partial unique index keys (MatchId, Step) where Answer='Pending',
        // so prior already-claimed JobCompleted rows (e.g. previous InProgress) do not
        // block a fresh recheck Pending row.
        var entry = new MatchFeedback { MatchId = match.Id, Step = FeedbackStep.JobCompleted };
        if (!await feedback.TryAddPendingAsync(entry, ct)) return;

        var ctx = new ReplyContext(
            Purpose: "feedback-step-2-job-completed",
            RecentTurns: Array.Empty<ConversationTurn>(),
            LanguageHint: "en",
            Facts: new Dictionary<string, string>
            {
                ["service"] = request.ServiceSlug,
                ["instruction"] = "Ask whether the job is completed. Possible answers: YES, NO, IN PROGRESS."
            });
        try
        {
            var reply = await AiReplyHelper.TryGenerateAsync(ai, ctx, "step2_feedback", logger, ct);
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

        logger.LogInformation("Step2 feedback prompted for match {MatchId}", match.Id);
    }

    private async Task SafeDeleteAsync(Guid feedbackId, CancellationToken ct)
    {
        var deleted = await feedback.DeletePendingAsync(feedbackId, ct);
        if (!deleted)
            logger.LogWarning("DeletePendingAsync returned false for Step2 {FeedbackId}", feedbackId);
    }
}
