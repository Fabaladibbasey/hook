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
        var prior = await feedback.GetByIdAsync(evt.FeedbackId, ct);
        if (prior is null || prior.Step != FeedbackStep.DidYouFind) return;
        if (prior.Answer != FeedbackAnswer.Yes) return;

        var match = await matches.GetAsync(prior.MatchId, ct);
        if (match is null) return;
        var request = await requests.GetAsync(match.RequestId, ct);
        if (request is null) return;
        if (!PhoneNumber.TryParse(request.ClientPhone, out var clientPhone)) return;

        // Mirror Step1: reserve the pending row first so concurrent fires can't
        // double-prompt the client. Build context + AI + send only after the claim.
        // No precheck — TryAddPendingAsync's 23505 catch is the sole guard.
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
        var reply = await AiReplyHelper.TryGenerateAsync(ai, ctx, "step2_feedback", logger, ct);
        if (reply is null) return;

        try
        {
            await whatsapp.SendTextAsync(clientPhone, reply, ct);
        }
        catch
        {
            await feedback.DeletePendingAsync(entry.Id, ct);
            throw;
        }

        logger.LogInformation("Step2 feedback prompted for match {MatchId}", match.Id);
    }
}
