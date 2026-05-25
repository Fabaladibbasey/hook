using Hook.Features.Feedback.Models;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.Feedback.Step1Prompt;

public sealed class Step1RecheckHandler(
    IFeedbackRepository feedback,
    IOptions<FeedbackOptions> options,
    TimeProvider clock,
    ILogger<Step1RecheckHandler> logger)
{
    public async Task Handle(Step1RecheckCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        var pending = await feedback.GetPendingAsync(cmd.MatchId, FeedbackStep.DidYouFind, ct);
        if (pending is null) return;

        var now = clock.GetUtcNow();
        var opts = options.Value;

        // Defense-in-depth: align dispatcher with the user-reply cap. Replay storms
        // or future schedulers must not spam past the cap.
        if (pending.Step1RecheckCount > opts.Step1MaxRechecks)
        {
            pending.ClaimSkipped(now);
            await feedback.TrySaveAsync(pending, ct);
            return;
        }

        // Atomic re-prompt guard: a back-to-back recheck (scheduled + opportunistic)
        // both reading the same Pending row collapse to one prompt send. Loser exits
        // without dispatch.
        if (!pending.Reprompt(now, opts.MinRecheckGap)) return;
        if (!await feedback.TrySaveAsync(pending, ct)) return;

        // Prompt-format data was denormalised onto the command at schedule time so
        // the scheduled-dispatch path runs zero reads.
        await bus.PublishAsync(new Step1PromptDispatchCommand(
            FeedbackId: pending.Id,
            MatchId: cmd.MatchId,
            RequestId: pending.RequestId,
            ClientPhone: cmd.ClientPhone,
            ServiceSlug: cmd.ServiceSlug,
            PickedFormatted: cmd.PickedFormatted));

        logger.LogDebug(
            "Step1 recheck re-prompted for match {MatchId}, recheck count {Count}",
            cmd.MatchId, pending.Step1RecheckCount);
    }
}
