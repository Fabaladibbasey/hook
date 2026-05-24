using System.Text.RegularExpressions;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback.Eta;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Features.Feedback.Step1Intent;
using Hook.Features.Feedback.Step1Prompt;
using Hook.Features.Feedback.Step2Intent;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Core;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.Feedback.AggregateStats;

public sealed class FeedbackResponseService(
    IFeedbackRepository feedback,
    IMatchRepository matches,
    IEventBus events,
    IMessageBus bus,
    IOptions<FeedbackOptions> options,
    TimeProvider clock,
    ILogger<FeedbackResponseService> logger)
{
    private static readonly Regex InProgressRegex = new(
        @"\bin\s+progress\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NotInProgressRegex = new(
        @"\bnot\s+in\s+progress\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DigitRegex = new(
        @"\b(\d{1,2})\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EtaKeywordRegex = new(
        @"\b(today|tomorrow|tonight|morning|afternoon|evening|night|now)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task HandleAsync(
        InboundMessage msg,
        MatchFeedback pending,
        CancellationToken ct) => pending.Step switch
        {
            FeedbackStep.DidYouFind => HandleDidYouFindAsync(msg, pending, ct),
            FeedbackStep.IdentifyWinner => HandleIdentifyWinnerAsync(msg, pending, ct),
            FeedbackStep.JobCompleted => HandleJobCompletedAsync(msg, pending, ct),
            FeedbackStep.AwaitingEta => HandleAwaitingEtaAsync(msg, pending, ct),
            FeedbackStep.CaptureNoReason => HandleCaptureNoReasonAsync(msg, pending, ct),
            _ => throw new InvalidOperationException($"Unhandled FeedbackStep '{pending.Step}'")
        };

    private async Task HandleDidYouFindAsync(
        InboundMessage msg, MatchFeedback pending, CancellationToken ct)
    {
        var text = msg.Text ?? string.Empty;

        // Layered parser: deterministic first, AI fallback last. StopAsking is
        // checked before Yes/No because the bare literal "stop" maps to Rejection
        // under QuickIntent.Detect and would otherwise route to No.
        Step1ReplyIntent? quick = null;
        if (QuickIntent.DetectStop(text)) quick = Step1ReplyIntent.StopAsking;
        else
        {
            var classified = QuickIntent.Detect(text);
            if (classified == IntentKind.Confirmation) quick = Step1ReplyIntent.Yes;
            else if (classified == IntentKind.Rejection) quick = Step1ReplyIntent.No;
            else if (QuickIntent.DetectReschedule(text)) quick = Step1ReplyIntent.Reschedule;
        }
        if (quick is { } intent)
        {
            await ApplyStep1IntentAsync(pending, msg.From, intent, null, ct);
            return;
        }

        // AI fallback off the user-visible critical path. Stale-window short-circuit
        // routes through Apply(Unclear) so the stale-Skip + ack copy lives in one place.
        var now = clock.GetUtcNow();
        if (now - pending.PromptedAt > options.Value.ParseRetryWindow)
        {
            await ApplyStep1IntentAsync(pending, msg.From, Step1ReplyIntent.Unclear, null, ct);
            return;
        }

        await bus.PublishAsync(new ExtractStep1IntentCommand(
            pending.Id, pending.MatchId,
            ScrubForOutbox(text, options.Value.OutboxTextMaxChars),
            pending.PromptedAt));
    }

    internal async Task ApplyStep1IntentAsync(
        MatchFeedback pending,
        PhoneNumber from,
        Step1ReplyIntent intent,
        DateTimeOffset? eta,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var opts = options.Value;

        switch (intent)
        {
            case Step1ReplyIntent.Yes:
                await HandleStep1YesAsync(pending, from, now, ct);
                return;

            case Step1ReplyIntent.No:
                // Reserve CaptureNoReason first, then publish prompt, then claim Step1.
                // Reserve-then-publish-then-claim mirrors HandleStep1YesAsync's multi-pick
                // branch: on a mid-method crash the inbound re-enters, the deterministic
                // No re-fires, the unique (MatchId, Step) partial index collides cleanly
                // on the prior CaptureNoReason row, and we exit silently — the prior run
                // already sent the prompt the user sees.
                var captureRow = MatchFeedback.CreatePending(
                    pending.MatchId, pending.RequestId, FeedbackStep.CaptureNoReason, now);
                if (!await feedback.TryAddPendingAsync(captureRow, ct)) return;
                await bus.PublishAsync(new SendWhatsAppTextCommand(from, FeedbackCopy.Step1NoAsk));
                await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.No, now, ct);
                return;

            case Step1ReplyIntent.Reschedule:
                if (eta is { } etaValue)
                {
                    // Cap the requested delay at MaxEtaHorizon. The user said
                    // "ask me in a month" — treat that as intent to defer, not
                    // intent to stop. We still commit to one bounded recheck.
                    var requestedDelay = etaValue - now;
                    var cappedDelay = requestedDelay > opts.MaxEtaHorizon ? opts.MaxEtaHorizon : requestedDelay;
                    if (!await feedback.TryRescheduleAsync(pending.Id, now, ct)) return;
                    var delay = cappedDelay + opts.EtaScheduleBuffer;
                    if (delay < opts.EtaScheduleBuffer) delay = opts.EtaScheduleBuffer;
                    await events.ScheduleAsync(new Step1RecheckCommand(pending.MatchId), delay, ct);
                    await bus.PublishAsync(new SendWhatsAppTextCommand(from,
                        FeedbackCopy.CheckBackIn(Humanize(delay))));
                    return;
                }

                // No-eta reschedule: walk the ladder. Cap-exceeded → claim Skipped silently.
                var nextCount = pending.Step1RecheckCount + 1;
                if (nextCount > opts.Step1MaxRechecks)
                {
                    await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.Skipped, now, ct);
                    return;
                }
                if (!await feedback.TryRescheduleAsync(pending.Id, now, ct)) return;
                var rung = opts.Step1RecheckSchedule[Math.Min(nextCount - 1, opts.Step1RecheckSchedule.Count - 1)];
                await events.ScheduleAsync(new Step1RecheckCommand(pending.MatchId), rung, ct);
                await bus.PublishAsync(new SendWhatsAppTextCommand(from,
                    FeedbackCopy.CheckBackIn(Humanize(rung))));
                return;

            case Step1ReplyIntent.StopAsking:
                await ClaimStopAndAckAsync(pending, from, now, ct);
                return;

            case Step1ReplyIntent.Unclear:
                if (now - pending.PromptedAt > opts.ParseRetryWindow)
                {
                    if (!await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.Skipped, now, ct)) return;
                    await bus.PublishAsync(new SendWhatsAppTextCommand(from, FeedbackCopy.SkippedAck));
                    return;
                }
                await bus.PublishAsync(new SendWhatsAppTextCommand(from, FeedbackCopy.Step1UnclearRetry));
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unhandled Step1ReplyIntent");
        }
    }

    private async Task ClaimStopAndAckAsync(
        MatchFeedback pending, PhoneNumber from, DateTimeOffset now, CancellationToken ct)
    {
        if (!await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.Skipped, now, ct)) return;
        await bus.PublishAsync(new SendWhatsAppTextCommand(from, FeedbackCopy.StopAck));
    }

    private async Task HandleStep1YesAsync(
        MatchFeedback pending, PhoneNumber from, DateTimeOffset now, CancellationToken ct)
    {
        var picked = await GetPickedMatchesAsync(pending.MatchId, ct);

        if (picked.Count == 0)
        {
            // Race: anchor's PickedAt was cleared between Step1 schedule and this reply.
            // Claim and exit — no winner to dispatch.
            await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.Yes, now, ct);
            logger.LogWarning("Step1 Yes for match {MatchId} but no picked siblings", pending.MatchId);
            return;
        }

        if (picked.Count == 1)
        {
            if (!await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.Yes, now, ct)) return;
            await events.PublishAsync(new Step2FeedbackCheck(pending.MatchId), ct);
            return;
        }

        // Multi-pick: reserve IdentifyWinner first, then publish the prompt envelope
        // and claim Step1. Reserve-then-publish guarantees that on retry the Pending
        // row is already there and TryAddPendingAsync's unique-collision short-circuits
        // cleanly.
        var winner = MatchFeedback.CreatePending(
            pending.MatchId, pending.RequestId, FeedbackStep.IdentifyWinner, now);
        if (!await feedback.TryAddPendingAsync(winner, ct)) return;

        await bus.PublishAsync(new SendWhatsAppTextCommand(from,
            FeedbackCopy.IdentifyWinnerPrompt(PickedMatchListFormatter.Format(picked))));

        await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.Yes, now, ct);
    }

    private async Task HandleCaptureNoReasonAsync(
        InboundMessage msg, MatchFeedback pending, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (now - pending.PromptedAt > options.Value.ParseRetryWindow)
        {
            // Stale CaptureNoReason row — claim silently so it cannot swallow an
            // unrelated future inbound and persist arbitrary text into NoReason.
            await feedback.TryClaimNoReasonAsync(pending.Id, null, now, ct);
            return;
        }

        var text = msg.Text ?? string.Empty;
        var trimmed = text.Trim();
        var isSkip = trimmed.StartsWith("skip", StringComparison.OrdinalIgnoreCase) && trimmed.Length <= 5;
        var scrubbed = isSkip ? trimmed : PhoneScrubRx.Replace(trimmed, "[phone]");
        string? reason = isSkip
            ? null
            : scrubbed.Length > FeedbackConstants.NoReasonMaxLength
                ? scrubbed[..FeedbackConstants.NoReasonMaxLength]
                : scrubbed;

        if (!await feedback.TryClaimNoReasonAsync(pending.Id, reason, now, ct)) return;
        await bus.PublishAsync(new SendWhatsAppTextCommand(msg.From, FeedbackCopy.CaptureNoReasonAck));
    }

    // Strip E.164-ish phone numbers from free-text fields before persisting so
    // user-shared third-party phones do not accumulate at rest (NoReason rows
    // + ExtractStep{1,2}IntentCommand envelope bodies).
    internal static readonly Regex PhoneScrubRx = new(
        @"\+?\(?\d[\d\s().\-]{7,19}\d", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static string ScrubForOutbox(string text, int maxChars)
    {
        var scrubbed = PhoneScrubRx.Replace(text, "[phone]");
        return scrubbed.Length > maxChars ? scrubbed[..maxChars] : scrubbed;
    }

    // Compact, user-friendly time formatter for the reschedule ack. Truncates
    // hours to integer + carries the remainder as minutes so a 90-min recheck
    // reads as "1h 30m" instead of rounding to "2 hours".
    internal static string Humanize(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            var hours = (int)span.TotalHours;
            var minutes = span.Minutes;
            if (minutes == 0) return hours == 1 ? "1 hour" : $"{hours} hours";
            return $"{hours}h {minutes}m";
        }
        var mins = Math.Max(1, (int)Math.Ceiling(span.TotalMinutes));
        return mins == 1 ? "1 minute" : $"{mins} minutes";
    }

    private async Task HandleIdentifyWinnerAsync(InboundMessage msg, MatchFeedback pending, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var opts = options.Value;
        var text = msg.Text ?? string.Empty;

        var picked = await GetPickedMatchesAsync(pending.MatchId, ct);
        if (picked.Count == 0) return;

        var pickIndex = ParsePickDigit(text, picked.Count);
        if (pickIndex is null)
        {
            if (now - pending.PromptedAt > opts.ParseRetryWindow)
            {
                logger.LogWarning("IdentifyWinner parse window expired for match {MatchId}", pending.MatchId);
                if (!await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.Skipped, now, ct)) return;
                await bus.PublishAsync(new SendWhatsAppTextCommand(msg.From, FeedbackCopy.SkippedAck));
                return;
            }
            await SendRetryHintIfFreshAsync(msg, pending, now, opts,
                FeedbackCopy.IdentifyWinnerRetryHintBody(PickedMatchListFormatter.Format(picked)), ct);
            return;
        }

        if (!await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.WinnerSelected, now, ct)) return;

        var winnerMatch = picked[pickIndex.Value - 1];
        await events.PublishAsync(new Step2FeedbackCheck(winnerMatch.Id), ct);
    }

    private async Task HandleJobCompletedAsync(
        InboundMessage msg, MatchFeedback pending, CancellationToken ct)
    {
        var text = msg.Text ?? string.Empty;
        var now = clock.GetUtcNow();
        var opts = options.Value;

        // Layer 1: deterministic.
        var (quick, extractedEta) = TryQuickClassifyStep2(text, now);
        if (quick is { } intent)
        {
            await ApplyStep2IntentAsync(pending, msg.From, intent, extractedEta, ct);
            return;
        }

        // Layer 2: stale-window short-circuit through Apply(Unclear) so the
        // stale-Skip + ack copy lives in one place.
        if (now - pending.PromptedAt > opts.ParseRetryWindow)
        {
            await ApplyStep2IntentAsync(pending, msg.From, Step2ReplyIntent.Unclear, null, ct);
            return;
        }

        // Layer 3: AI fallback off the user-visible critical path.
        await bus.PublishAsync(new ExtractStep2IntentCommand(
            pending.Id, pending.MatchId,
            ScrubForOutbox(text, opts.OutboxTextMaxChars),
            pending.PromptedAt));
    }

    internal async Task ApplyStep2IntentAsync(
        MatchFeedback pending,
        PhoneNumber from,
        Step2ReplyIntent intent,
        DateTimeOffset? eta,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var opts = options.Value;

        switch (intent)
        {
            case Step2ReplyIntent.Yes:
                if (!await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.Yes, now, ct)) return;
                await bus.PublishAsync(new SendWhatsAppTextCommand(from, FeedbackCopy.Step2YesAck));
                await RecordOutcomeAsync(pending.MatchId, FeedbackAnswer.Yes, now, ct);
                return;

            case Step2ReplyIntent.No:
                if (!await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.No, now, ct)) return;
                await bus.PublishAsync(new SendWhatsAppTextCommand(from, FeedbackCopy.Step2NoAck));
                await RecordOutcomeAsync(pending.MatchId, FeedbackAnswer.No, now, ct);
                return;

            case Step2ReplyIntent.InProgress:
                if (eta is { } etaValue)
                {
                    // Cap the requested delay at MaxEtaHorizon — mirrors Step1 Reschedule.
                    var requestedDelay = etaValue - now;
                    var cappedDelay = requestedDelay > opts.MaxEtaHorizon ? opts.MaxEtaHorizon : requestedDelay;
                    var cappedEta = now + cappedDelay;
                    if (!await feedback.TryClaimPendingWithEtaAsync(
                            pending.Id, FeedbackAnswer.EtaCaptured, cappedEta, now, ct)) return;
                    var delay = cappedDelay + opts.EtaScheduleBuffer;
                    if (delay < opts.EtaScheduleBuffer) delay = opts.EtaScheduleBuffer;
                    await events.ScheduleAsync(new Step2FeedbackCheck(pending.MatchId), delay, ct);
                    await bus.PublishAsync(new SendWhatsAppTextCommand(from,
                        FeedbackCopy.CheckBackIn(Humanize(delay))));
                    return;
                }

                // No usable eta: reserve AwaitingEta first, send prompt, then claim InProgress.
                // Reserve-then-publish-then-claim mirrors Step1 No so a mid-method crash re-enters
                // through the partial unique index instead of double-prompting.
                var awaitingRow = MatchFeedback.CreatePending(
                    pending.MatchId, pending.RequestId, FeedbackStep.AwaitingEta, now);
                if (!await feedback.TryAddPendingAsync(awaitingRow, ct)) return;
                await bus.PublishAsync(new SendWhatsAppTextCommand(from, FeedbackCopy.Step2AwaitingEtaAsk));
                await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.InProgress, now, ct);
                return;

            case Step2ReplyIntent.StopAsking:
                await ClaimStopAndAckAsync(pending, from, now, ct);
                return;

            case Step2ReplyIntent.Unclear:
                if (now - pending.PromptedAt > opts.ParseRetryWindow)
                {
                    if (!await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.Skipped, now, ct)) return;
                    await bus.PublishAsync(new SendWhatsAppTextCommand(from, FeedbackCopy.SkippedAck));
                    return;
                }
                await bus.PublishAsync(new SendWhatsAppTextCommand(from, FeedbackCopy.Step2UnclearRetry));
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unhandled Step2ReplyIntent");
        }
    }

    private async Task HandleAwaitingEtaAsync(InboundMessage msg, MatchFeedback pending, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var opts = options.Value;
        var text = msg.Text ?? string.Empty;

        // Deterministic precheck: short replies with no digits and no ETA keyword
        // can never carry a future-time signal — handle inline without a round-trip
        // to Ollama. Candidates defer to the outbox so the 60-150s Ollama window
        // does not block the inbound funnel.
        if (!LooksLikeEtaCandidate(text))
        {
            if (now - pending.PromptedAt <= opts.ParseRetryWindow)
            {
                await bus.PublishAsync(new SendWhatsAppTextCommand(msg.From, FeedbackCopy.AwaitingEtaRetry));
                return;
            }
            await ClaimSkippedAndFallbackAsync(pending, opts, now, msg.From, ct);
            return;
        }

        await bus.PublishAsync(new SendWhatsAppTextCommand(msg.From, FeedbackCopy.AwaitingEtaGotIt));
        await bus.PublishAsync(new ExtractEtaCommand(pending.Id, pending.MatchId, msg.From, text));
    }

    internal async Task ClaimSkippedAndFallbackAsync(
        MatchFeedback pending,
        FeedbackOptions opts,
        DateTimeOffset now,
        PhoneNumber from,
        CancellationToken ct)
    {
        if (!await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.Skipped, now, ct)) return;
        await events.ScheduleAsync(new Step2FeedbackCheck(pending.MatchId), opts.Step2InProgressRecheckDelay, ct);
        await bus.PublishAsync(new SendWhatsAppTextCommand(from, FeedbackCopy.SkippedAck));
        logger.LogInformation(
            "ETA unusable for match {MatchId}; Step2 recheck scheduled at +{Delay}",
            pending.MatchId, opts.Step2InProgressRecheckDelay);
    }

    // Parses a positional pick reply ("2", "I pick 2", "3."). Internal so unit tests
    // can drive the table-driven cases without going through HandleIdentifyWinnerAsync.
    internal static int? ParsePickDigit(string text, int max)
    {
        var match = DigitRegex.Match(text);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, out var n)) return null;
        return n >= 1 && n <= max ? n : null;
    }

    // Layered Step2 classifier. StopAsking before Yes/No — bare literal "stop" maps to
    // Rejection under QuickIntent.Detect and would otherwise route to No. InProgress
    // branches also try to extract a relative-duration ETA so the user does not have
    // to repeat themselves.
    internal static (Step2ReplyIntent? Intent, DateTimeOffset? Eta) TryQuickClassifyStep2(
        string text, DateTimeOffset now)
    {
        if (QuickIntent.DetectStop(text)) return (Step2ReplyIntent.StopAsking, null);

        var classified = QuickIntent.Detect(text);
        if (classified == IntentKind.Confirmation) return (Step2ReplyIntent.Yes, null);
        if (classified == IntentKind.Rejection) return (Step2ReplyIntent.No, null);

        var lower = text.Trim().ToLowerInvariant();
        if (NotInProgressRegex.IsMatch(lower)) return (Step2ReplyIntent.No, null);
        if (InProgressRegex.IsMatch(lower))
            return (Step2ReplyIntent.InProgress, QuickIntent.TryExtractRelativeEta(text, now));
        if (QuickIntent.DetectInProgress(text))
            return (Step2ReplyIntent.InProgress, QuickIntent.TryExtractRelativeEta(text, now));

        return (null, null);
    }

    private static bool LooksLikeEtaCandidate(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 4) return false;
        if (text.Any(char.IsDigit)) return true;
        return EtaKeywordRegex.IsMatch(text);
    }

    private async Task<IReadOnlyList<Matching.MatchAggregate.Match>> GetPickedMatchesAsync(
        Guid anchorMatchId,
        CancellationToken ct)
    {
        var anchor = await matches.GetAsync(anchorMatchId, ct);
        if (anchor is null) return [];
        // Repository orders by Score DESC, DistanceKm, CreatedAt, Id — same as the
        // MatchPresenter "PICK 1/2/3" enumeration the client originally saw, so a
        // positional reply ("2") still resolves to the right Match. Filter is in
        // SQL via ix_matches_request_picked_at.
        return await matches.GetPickedForRequestAsync(anchor.RequestId, ct);
    }

    private async Task SendRetryHintIfFreshAsync(
        InboundMessage msg,
        MatchFeedback pending,
        DateTimeOffset now,
        FeedbackOptions opts,
        string hint,
        CancellationToken ct)
    {
        // Bound the spammy retry prompt to ParseRetryWindow so a forgotten Pending row
        // can't re-arm "didn't catch that" replies indefinitely on every inbound.
        if (now - pending.PromptedAt > opts.ParseRetryWindow) return;
        await bus.PublishAsync(new SendWhatsAppTextCommand(msg.From, $"Sorry, didn't catch that. {hint}"));
    }

    private async Task RecordOutcomeAsync(Guid matchId, FeedbackAnswer answer, DateTimeOffset now, CancellationToken ct)
    {
        var match = await matches.GetAsync(matchId, ct);
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

internal static class FeedbackCopy
{
    public const string Step1NoAsk =
        "Thanks. What made it hard — nobody replied, prices, distance, something else? (or reply SKIP)";
    public const string Step1UnclearRetry =
        "Sorry, didn't catch that. Reply YES if you found a provider, NO if you didn't, "
        + "or LATER if you'd like us to ask again.";
    public const string Step2YesAck = "Glad it worked out — thanks for the feedback!";
    public const string Step2NoAck = "Thanks for letting us know. We'll factor that into future matches.";
    public const string Step2AwaitingEtaAsk =
        "Got it — when do you think it'll be done? e.g. 'in 3 hours' or 'tomorrow at 5pm'.";
    public const string Step2UnclearRetry =
        "Sorry, didn't catch that. Reply YES if the job is done, NO if it didn't happen, "
        + "or IN PROGRESS if you're still working on it.";
    public const string StopAck = "Got it — we won't ask about this one again.";
    public const string SkippedAck = "Thanks — recorded that. No more questions on this one.";
    public const string CaptureNoReasonAck = "Thanks — that helps us improve.";
    public const string AwaitingEtaGotIt = "Got your ETA — one sec…";
    public const string AwaitingEtaRetry =
        "Sorry, didn't catch that. When do you think the job will be done? "
        + "e.g. 'in 3 hours' or 'tomorrow at 5pm'.";

    public static string CheckBackIn(string humanizedDelay) =>
        $"Got it, we'll check back in {humanizedDelay}.";

    public static string IdentifyWinnerPrompt(string list) =>
        $"Which provider worked out? Reply with the number — \n{list}.";

    // No "Sorry, didn't catch that." prefix — SendRetryHintIfFreshAsync prepends it.
    public static string IdentifyWinnerRetryHintBody(string list) =>
        $"Reply with the number of the provider that worked out — {list}.";
}
