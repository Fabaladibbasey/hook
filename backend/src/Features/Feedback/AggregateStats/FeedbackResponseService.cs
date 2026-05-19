using System.Text.RegularExpressions;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
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
    IConversationAi ai,
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
        LazyIntent intent,
        CancellationToken ct) => pending.Step switch
        {
            FeedbackStep.DidYouFind => HandleDidYouFindAsync(msg, pending, intent, ct),
            FeedbackStep.IdentifyWinner => HandleIdentifyWinnerAsync(msg, pending, ct),
            FeedbackStep.JobCompleted => HandleJobCompletedAsync(msg, pending, intent, ct),
            FeedbackStep.AwaitingEta => HandleAwaitingEtaAsync(msg, pending, ct),
            _ => throw new InvalidOperationException($"Unhandled FeedbackStep '{pending.Step}'")
        };

    private async Task HandleDidYouFindAsync(
        InboundMessage msg, MatchFeedback pending, LazyIntent intent, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var opts = options.Value;
        var text = msg.Text ?? string.Empty;

        // ParseAnswer can return InProgress (shared with JobCompleted) but at Step1 only
        // Yes/No are real answers — collapse so the user gets the retry hint instead of
        // a silent InProgress claim that traps them.
        var parsed = ParseAnswer(text);
        if (parsed is FeedbackAnswer.InProgress) parsed = null;

        // Bound the AI fallback by the retry window — hostile garbage on a stale Pending
        // shouldn't keep dragging Ollama into the loop.
        if (parsed is null && now - pending.PromptedAt > opts.ParseRetryWindow)
        {
            if (!await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.Skipped, now, ct)) return;
            await bus.PublishAsync(new SendWhatsAppTextRequested(msg.From,
                "Thanks — recorded that. No more questions on this one."));
            return;
        }

        var answer = parsed ?? await ResolveIntentYesNoAsync(intent, ct);
        if (answer is null)
        {
            await SendRetryHintIfFreshAsync(msg, pending, now, opts,
                "Reply YES if you found a provider, or NO if you didn't.", ct);
            return;
        }

        if (answer != FeedbackAnswer.Yes)
        {
            if (!await feedback.TryClaimPendingAsync(pending.Id, answer.Value, now, ct)) return;
            await bus.PublishAsync(new SendWhatsAppTextRequested(msg.From,
                "Thanks for letting us know. We'll keep looking for someone better next time."));
            return;
        }

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

        var prompt = $"Which provider worked out? Reply with the number — {PickedMatchListFormatter.Format(picked)}.";
        await bus.PublishAsync(new SendWhatsAppTextRequested(msg.From, prompt));

        await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.Yes, now, ct);
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
                await bus.PublishAsync(new SendWhatsAppTextRequested(msg.From,
                    "Thanks — recorded that. No more questions on this one."));
                return;
            }
            await SendRetryHintIfFreshAsync(msg, pending, now, opts,
                $"Reply with the number of the provider that worked out — {PickedMatchListFormatter.Format(picked)}.", ct);
            return;
        }

        if (!await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.WinnerSelected, now, ct)) return;

        var winnerMatch = picked[pickIndex.Value - 1];
        await events.PublishAsync(new Step2FeedbackCheck(winnerMatch.Id), ct);
    }

    private async Task HandleJobCompletedAsync(
        InboundMessage msg, MatchFeedback pending, LazyIntent intent, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var opts = options.Value;
        var text = msg.Text ?? string.Empty;

        var parsed = ParseAnswer(text);
        if (parsed is null && now - pending.PromptedAt > opts.ParseRetryWindow)
        {
            if (!await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.Skipped, now, ct)) return;
            await bus.PublishAsync(new SendWhatsAppTextRequested(msg.From,
                "Thanks — recorded that. No more questions on this one."));
            return;
        }

        // AI intent fallback only resolves Confirmation/Rejection; InProgress comes
        // from ParseAnswer's regex.
        var answer = parsed ?? await ResolveIntentYesNoAsync(intent, ct);
        if (answer is null)
        {
            await SendRetryHintIfFreshAsync(msg, pending, now, opts,
                "Reply YES if the job is done, NO if it didn't happen, or IN PROGRESS if you're still working on it.", ct);
            return;
        }

        if (!await feedback.TryClaimPendingAsync(pending.Id, answer.Value, now, ct)) return;

        if (answer == FeedbackAnswer.InProgress)
        {
            // Pillar B: reserve AwaitingEta and ask the client when they expect to
            // finish. The next inbound is routed through HandleAwaitingEtaAsync;
            // failure to parse falls back to Step2InProgressRecheckDelay.
            var eta = MatchFeedback.CreatePending(
                pending.MatchId, pending.RequestId, FeedbackStep.AwaitingEta, now);
            if (!await feedback.TryAddPendingAsync(eta, ct)) return;

            await bus.PublishAsync(new SendWhatsAppTextRequested(msg.From,
                "Got it — when do you think it'll be done? e.g. 'in 3 hours' or 'tomorrow at 5pm'."));
            return;
        }

        var step2Ack = answer == FeedbackAnswer.Yes
            ? "Glad it worked out — thanks for the feedback!"
            : "Thanks for letting us know. We'll factor that into future matches.";
        await bus.PublishAsync(new SendWhatsAppTextRequested(msg.From, step2Ack));
        await RecordOutcomeAsync(pending.MatchId, answer.Value, now, ct);
    }

    private async Task HandleAwaitingEtaAsync(InboundMessage msg, MatchFeedback pending, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var opts = options.Value;
        var text = msg.Text ?? string.Empty;

        // Deterministic precheck: short replies with no digits and no ETA keyword
        // can never carry a future-time signal — skip the round-trip to Ollama.
        var eta = LooksLikeEtaCandidate(text)
            ? await TryExtractEtaAsync(text, now, ct)
            : null;

        if (eta is { } etaValue)
        {
            // Cap absurd ETAs (parse hallucination, year 2099) so we don't sleep on a
            // recheck for weeks. Treat as no-ETA and fall back.
            if (etaValue - now > opts.MaxEtaHorizon)
            {
                logger.LogWarning(
                    "ETA {Eta} for match {MatchId} exceeds MaxEtaHorizon ({Horizon}); falling back",
                    etaValue, pending.MatchId, opts.MaxEtaHorizon);
                await ClaimSkippedAndFallbackAsync(pending, opts, now, msg.From, ct);
                return;
            }

            if (!await feedback.TryClaimPendingWithEtaAsync(
                    pending.Id, FeedbackAnswer.EtaCaptured, etaValue, now, ct)) return;
            var delay = etaValue - now + opts.EtaScheduleBuffer;
            if (delay < TimeSpan.Zero) delay = opts.EtaScheduleBuffer;
            await events.ScheduleAsync(new Step2FeedbackCheck(pending.MatchId), delay, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(msg.From,
                "Got it — I'll check back with you after that. Good luck!"));
            logger.LogInformation(
                "ETA captured for match {MatchId}, Step2 recheck scheduled at +{Delay}",
                pending.MatchId, delay);
            return;
        }

        if (now - pending.PromptedAt <= opts.ParseRetryWindow)
        {
            await bus.PublishAsync(new SendWhatsAppTextRequested(msg.From,
                "Sorry, didn't catch that. When do you think the job will be done? e.g. 'in 3 hours' or 'tomorrow at 5pm'."));
            return;
        }

        await ClaimSkippedAndFallbackAsync(pending, opts, now, msg.From, ct);
    }

    private async Task ClaimSkippedAndFallbackAsync(
        MatchFeedback pending, FeedbackOptions opts, DateTimeOffset now, PhoneNumber from, CancellationToken ct)
    {
        if (!await feedback.TryClaimPendingAsync(pending.Id, FeedbackAnswer.Skipped, now, ct)) return;
        await events.ScheduleAsync(new Step2FeedbackCheck(pending.MatchId), opts.Step2InProgressRecheckDelay, ct);
        await bus.PublishAsync(new SendWhatsAppTextRequested(from,
            "Thanks — recorded that. No more questions on this one."));
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

    private static async Task<FeedbackAnswer?> ResolveIntentYesNoAsync(LazyIntent intent, CancellationToken ct)
    {
        var detected = await intent.GetAsync(ct);
        return detected.Intent switch
        {
            IntentKind.Confirmation => FeedbackAnswer.Yes,
            IntentKind.Rejection => FeedbackAnswer.No,
            _ => null
        };
    }

    private static bool LooksLikeEtaCandidate(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 4) return false;
        if (text.Any(char.IsDigit)) return true;
        return EtaKeywordRegex.IsMatch(text);
    }

    private async Task<DateTimeOffset?> TryExtractEtaAsync(string text, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            return await ai.ExtractEtaAsync(text, now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ETA extraction failed");
            return null;
        }
    }

    private async Task<IReadOnlyList<Hook.Features.Matching.MatchAggregate.Match>> GetPickedMatchesAsync(Guid anchorMatchId, CancellationToken ct)
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
        InboundMessage msg, MatchFeedback pending, DateTimeOffset now, FeedbackOptions opts, string hint, CancellationToken ct)
    {
        // Bound the spammy retry prompt to ParseRetryWindow so a forgotten Pending row
        // can't re-arm "didn't catch that" replies indefinitely on every inbound.
        if (now - pending.PromptedAt > opts.ParseRetryWindow) return;
        await bus.PublishAsync(new SendWhatsAppTextRequested(msg.From, $"Sorry, didn't catch that. {hint}"));
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
