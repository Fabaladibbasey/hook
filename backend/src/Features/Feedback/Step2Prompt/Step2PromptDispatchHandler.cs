using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Whatsapp;
using Wolverine.Attributes;

namespace Hook.Features.Feedback.Step2Prompt;

public sealed class Step2PromptDispatchHandler(
    IConversationAi ai,
    IWhatsappClient whatsapp,
    IFeedbackRepository feedback,
    ILogger<Step2PromptDispatchHandler> logger)
{
    [NonTransactional]
    public async Task Handle(Step2PromptDispatchRequested evt, CancellationToken ct)
    {
        var ctx = new ReplyContext(
            Purpose: "feedback-step-2-job-completed",
            RecentTurns: [],
            LanguageHint: "en")
        {
            Facts = new Dictionary<string, string>
            {
                ["service"] = evt.ServiceSlug,
                ["instruction"] = "Ask whether the job is completed. Possible answers: YES, NO, IN PROGRESS."
            }
        };

        var reply = await AiReplyHelper.TryGenerateAsync(
            ai, ctx, "step2_feedback", logger, ct, AiReplyHelper.NonCriticalReplyTimeout);
        if (reply is null)
        {
            await feedback.DeletePendingAsync(evt.FeedbackId, ct);
            return;
        }

        await whatsapp.SendTextAsync(evt.ClientPhone, reply, ct);
        logger.LogInformation("Step2 feedback prompted for match {MatchId}", evt.MatchId);
    }
}
