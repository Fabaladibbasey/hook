using System.Text.RegularExpressions;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;
using Wolverine;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;

namespace Hook.Features.Feedback.AggregateStats;

public sealed class FeedbackResponseService(
    IFeedbackRepository feedback,
    IMatchRepository matches,
    IMessageBus bus,
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
        if (answer is null) return;

        var now = clock.GetUtcNow();
        pending.Answer = answer.Value;
        pending.RepliedAt = now;
        await feedback.SaveChangesAsync(ct);

        if (pending.Step == FeedbackStep.DidYouFind && answer == FeedbackAnswer.Yes)
        {
            await bus.ScheduleAsync(new Step2FeedbackCheck(pending.Id), TimeSpan.FromHours(20));
        }

        if (pending.Step == FeedbackStep.JobCompleted)
        {
            if (answer == FeedbackAnswer.InProgress)
            {
                await bus.ScheduleAsync(new Step2FeedbackCheck(pending.Id), TimeSpan.FromHours(48));
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
