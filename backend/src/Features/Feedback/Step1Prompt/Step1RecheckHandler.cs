using Hook.Features.Feedback.Models;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Options;
using Wolverine;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;

namespace Hook.Features.Feedback.Step1Prompt;

public sealed class Step1RecheckHandler(
    IFeedbackRepository feedback,
    IMatchRepository matches,
    IServiceRequestRepository requests,
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
            await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.Skipped, now, ct);
            return;
        }

        var minGap = opts.MinRecheckGap;

        // Atomic re-prompt guard: a back-to-back recheck (scheduled + opportunistic)
        // both reading the same Pending row collapse to one prompt send. Loser exits
        // without dispatch.
        if (!await feedback.TryRepromptPendingAsync(pending.Id, now, minGap, ct)) return;

        var match = await matches.GetAsync(cmd.MatchId, ct);
        if (match is null) return;
        var request = await requests.GetAsync(match.RequestId, ct);
        if (request is null) return;
        if (!PhoneNumber.TryParse(request.ClientPhone, out var clientPhone)) return;

        var picked = await matches.GetPickedForRequestAsync(match.RequestId, ct);
        var pickedFormatted = picked.Count > 1 ? PickedMatchListFormatter.Format(picked) : string.Empty;

        await bus.PublishAsync(new Step1PromptDispatchCommand(
            FeedbackId: pending.Id,
            MatchId: match.Id,
            RequestId: match.RequestId,
            ClientPhone: clientPhone,
            ServiceSlug: request.ServiceSlug,
            PickedFormatted: pickedFormatted));

        logger.LogDebug(
            "Step1 recheck re-prompted for match {MatchId}, recheck count {Count}",
            cmd.MatchId, pending.Step1RecheckCount);
    }
}
