using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Whatsapp;

namespace Hook.Features.Feedback.Step1Prompt;

public sealed class Step1PromptDispatchHandler(
    IConversationAi ai,
    IWhatsappClient whatsapp,
    IFeedbackRepository feedback,
    ILogger<Step1PromptDispatchHandler> logger)
{
    public async Task Handle(Step1PromptDispatchRequested evt, CancellationToken ct)
    {
        var facts = new Dictionary<string, string> { ["service"] = evt.ServiceSlug };
        if (evt.PickedFormatted is not null)
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
            RecentTurns: Array.Empty<ConversationTurn>(),
            LanguageHint: "en",
            Facts: facts);

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
