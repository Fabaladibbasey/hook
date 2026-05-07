using System.Text.RegularExpressions;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Options;
using Wolverine;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;

namespace Hook.Features.Feedback.AggregateStats;

public sealed class FeedbackResponseService(
    IFeedbackRepository feedback,
    IMatchRepository matches,
    IMessageBus bus,
    IWhatsappClient whatsapp,
    IOptions<FeedbackOptions> options,
    TimeProvider clock,
    ILogger<FeedbackResponseService> logger)
{
    private static readonly Regex InProgressRegex = new(
        @"\bin\s+progress\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NotInProgressRegex = new(
        @"\bnot\s+in\s+progress\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task HandleAsync(
        InboundMessage msg,
        MatchFeedback pending,
        LazyIntent intent,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var opts = options.Value;

        var answer = ParseAnswer(msg.Text ?? string.Empty);
        if (answer is null)
        {
            var detected = await intent.GetAsync(ct);
            answer = detected.Intent switch
            {
                IntentKind.Confirmation => FeedbackAnswer.Yes,
                IntentKind.Rejection => FeedbackAnswer.No,
                _ => null
            };
        }
        if (answer is null)
        {
            // Bound the spammy retry prompt to ParseRetryWindow so a forgotten Pending row
            // can't re-arm "didn't catch that" replies indefinitely on every inbound.
            if (now - pending.PromptedAt > opts.ParseRetryWindow) return;

            var hint = pending.Step == FeedbackStep.DidYouFind
                ? "Reply YES if you found a provider, or NO if you didn't."
                : "Reply YES if the job is done, NO if it didn't happen, or IN PROGRESS if you're still working on it.";
            await whatsapp.SendTextAsync(msg.From, $"Sorry, didn't catch that. {hint}", ct);
            return;
        }

        if (!await feedback.TryClaimPendingAsync(pending.Id, answer.Value, now, ct))
        {
            return;
        }

        if (pending.Step == FeedbackStep.DidYouFind && answer == FeedbackAnswer.Yes)
        {
            await bus.ScheduleAsync(new Step2FeedbackCheck(pending.Id), opts.Step2InitialDelay);
        }

        if (pending.Step == FeedbackStep.JobCompleted)
        {
            if (answer == FeedbackAnswer.InProgress)
            {
                // Step2FeedbackCheck.FeedbackId points at the Step1 (DidYouFind) row;
                // passing the just-claimed JobCompleted id would fail the handler's
                // Step==DidYouFind guard and the recheck would silently no-op.
                var step1 = await feedback.GetLatestByMatchAndStepAsync(
                    pending.MatchId, FeedbackStep.DidYouFind, ct);
                if (step1 is not null)
                {
                    await bus.ScheduleAsync(new Step2FeedbackCheck(step1.Id), opts.Step2InProgressRecheckDelay);
                }
                return;
            }

            var match = await matches.GetAsync(pending.MatchId, ct);
            if (match is null) return;

            var existing = await feedback.GetStatsAsync(match.ProviderPhone, ct);
            var stats = existing ?? ProviderStats.Initial(match.ProviderPhone, now);
            stats.RecordOutcome(success: answer == FeedbackAnswer.Yes, now);
            await feedback.UpsertStatsAsync(stats, ct);
            var maskedProvider = PhoneNumber.TryParse(match.ProviderPhone, out var pn)
                ? pn.Mask()
                : "***";
            logger.LogInformation(
                "Provider {Provider} stats updated: success={Success}",
                maskedProvider,
                answer == FeedbackAnswer.Yes);
        }
    }

    internal static FeedbackAnswer? ParseAnswer(string text)
    {
        var lower = text.Trim().ToLowerInvariant();
        if (lower is "yes" or "y") return FeedbackAnswer.Yes;
        if (lower is "no" or "n") return FeedbackAnswer.No;
        if (NotInProgressRegex.IsMatch(lower)) return FeedbackAnswer.No;
        if (InProgressRegex.IsMatch(lower)) return FeedbackAnswer.InProgress;
        return null;
    }
}
