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

        // Reserve the pending row first so a concurrent invocation (Wolverine retry,
        // scheduler double-fire) loses the partial unique-index race and exits silently.
        // No precheck — TryAddPendingAsync's 23505 catch is the sole guard, saving a SELECT.
        var entry = new MatchFeedback { MatchId = match.Id, Step = FeedbackStep.DidYouFind };
        if (!await feedback.TryAddPendingAsync(entry, ct)) return;

        var ctx = new ReplyContext(
            Purpose: "feedback-step-1-did-you-find",
            RecentTurns: Array.Empty<ConversationTurn>(),
            LanguageHint: "en",
            Facts: new Dictionary<string, string>
            {
                ["service"] = request.ServiceSlug,
                ["instruction"] = "Ask if the client found a service provider. Mention they can reply YES or NO."
            });
        var reply = await AiReplyHelper.TryGenerateAsync(ai, ctx, "step1_feedback", logger, ct);
        if (reply is null) return;

        try
        {
            await whatsapp.SendTextAsync(clientPhone, reply, ct);
        }
        catch
        {
            // Drop the orphan pending row so a Wolverine retry can re-prompt cleanly;
            // otherwise the partial unique index would lock out future Step1 prompts
            // for this match forever.
            await feedback.DeletePendingAsync(entry.Id, ct);
            throw;
        }

        logger.LogInformation("Step1 feedback prompted for match {MatchId}", evt.MatchId);
    }
}
