using Hook.Features.Feedback.AggregateStats;
using Hook.Features.Feedback.Models;
using Hook.Shared.Core;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.Feedback.Eta;

public sealed class ApplyEtaHandler(
    IFeedbackRepository feedback,
    IEventBus events,
    FeedbackResponseService service,
    IOptions<FeedbackOptions> options,
    TimeProvider clock,
    ILogger<ApplyEtaHandler> logger)
{
    public async Task Handle(ApplyEtaCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        var pending = await feedback.GetByIdAsync(cmd.PendingId, ct);
        // Tight guard: the row must still be the AwaitingEta Pending we reserved.
        // Cross-step contamination (DidYouFind / JobCompleted retries hitting this handler)
        // would otherwise corrupt unrelated feedback state.
        if (pending is null
            || pending.Answer is not FeedbackAnswer.Pending
            || pending.Step != FeedbackStep.AwaitingEta) return;

        var now = clock.GetUtcNow();
        var opts = options.Value;

        if (cmd.EtaUtc is { } etaValue)
        {
            if (etaValue - now > opts.MaxEtaHorizon)
            {
                logger.LogWarning(
                    "ETA {Eta} for match {MatchId} exceeds MaxEtaHorizon ({Horizon}); falling back",
                    etaValue, cmd.MatchId, opts.MaxEtaHorizon);
                await service.ClaimSkippedAndFallbackAsync(pending, opts, now, cmd.From, ct);
                return;
            }

            pending.ClaimEta(etaValue, now);
            if (!await feedback.TrySaveAsync(pending, ct)) return;
            var delay = etaValue - now + opts.EtaScheduleBuffer;
            if (delay < TimeSpan.Zero) delay = opts.EtaScheduleBuffer;
            await events.ScheduleAsync(new Step2FeedbackCheck(cmd.MatchId), delay, ct);
            await bus.PublishAsync(new SendWhatsAppTextCommand(cmd.From,
                "Got it — I'll check back with you after that. Good luck!"));
            logger.LogInformation(
                "ETA captured for match {MatchId}, Step2 recheck scheduled at +{Delay}",
                cmd.MatchId, delay);
            return;
        }

        if (now - pending.PromptedAt <= opts.ParseRetryWindow)
        {
            await bus.PublishAsync(new SendWhatsAppTextCommand(cmd.From, FeedbackCopy.AwaitingEtaRetry));
            return;
        }

        await service.ClaimSkippedAndFallbackAsync(pending, opts, now, cmd.From, ct);
    }
}
