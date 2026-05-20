using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Whatsapp;
using Wolverine.Attributes;

namespace Hook.Features.Feedback.Step1Prompt;

public sealed class Step1PromptDispatchHandler(
    IConversationAi ai,
    IWhatsappClient whatsapp,
    IFeedbackRepository feedback,
    ILogger<Step1PromptDispatchHandler> logger)
{
    // AI inference takes 60-150s; the cleanup-on-null DeletePendingAsync runs
    // its own short tx via ExecuteDeleteAsync. Opt out of AutoApplyTransactions
    // so the handler doesn't pin a connection across the Ollama window.
    [NonTransactional]
    public async Task Handle(Step1PromptDispatchRequested evt, CancellationToken ct)
    {
        var facts = new Dictionary<string, string> { ["service"] = evt.ServiceSlug };
        if (!string.IsNullOrEmpty(evt.PickedFormatted))
        {
            facts["pickedProviders"] = evt.PickedFormatted;
            facts["instruction"] =
                "Ask if any of the providers we shared worked out. Reply YES (we'll ask which one) or NO.";
        }
        else
        {
            facts["instruction"] = "Ask if the client found a service provider. Mention they can reply YES or NO.";
        }

        var ctx = new ReplyContext(
            Purpose: "feedback-step-1-did-you-find",
            RecentTurns: [],
            LanguageHint: "en")
        {
            Facts = facts
        };

        var reply = await AiReplyHelper.TryGenerateAsync(ai, ctx, "step1_feedback", logger, ct);
        if (reply is null)
        {
            // AI-null is permanent (model declined); drop the Pending row so a future
            // Step1FeedbackCheck for this match isn't permanently blocked by the
            // partial unique index.
            await feedback.DeletePendingAsync(evt.FeedbackId, ct);
            return;
        }

        await whatsapp.SendTextAsync(evt.ClientPhone, reply, ct);
        logger.LogInformation(
            "Step1 feedback prompted for match {MatchId} request {RequestId}",
            evt.MatchId, evt.RequestId);
    }
}
