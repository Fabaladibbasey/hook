using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback;
using Hook.Features.Feedback.AggregateStats;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Core;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;

namespace Hook.UnitTests.Feedback;

public class FeedbackResponseServiceTests
{
    [Theory]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData("Y")]
    [InlineData("  yes  ")]
    public void ParseAnswer_YesVariants_ReturnsYes(string input) =>
        Assert.Equal(FeedbackAnswer.Yes, FeedbackResponseService.ParseAnswer(input));

    [Theory]
    [InlineData("no")]
    [InlineData("N")]
    [InlineData("NO")]
    public void ParseAnswer_NoVariants_ReturnsNo(string input) =>
        Assert.Equal(FeedbackAnswer.No, FeedbackResponseService.ParseAnswer(input));

    [Theory]
    [InlineData("in progress")]
    [InlineData("IN PROGRESS")]
    [InlineData("yes, in progress actually")]
    [InlineData("the work is in progress")]
    public void ParseAnswer_InProgress_ReturnsInProgress(string input) =>
        Assert.Equal(FeedbackAnswer.InProgress, FeedbackResponseService.ParseAnswer(input));

    [Theory]
    [InlineData("not in progress")]
    [InlineData("NOT IN PROGRESS")]
    [InlineData("the job is not in progress at all")]
    public void ParseAnswer_NotInProgress_ReturnsNo(string input) =>
        Assert.Equal(FeedbackAnswer.No, FeedbackResponseService.ParseAnswer(input));

    [Theory]
    [InlineData("")]
    [InlineData("maybe")]
    [InlineData("idk")]
    public void ParseAnswer_Unrecognised_ReturnsNull(string input) =>
        Assert.Null(FeedbackResponseService.ParseAnswer(input));

    [Fact]
    public void ParseAnswer_Whitespace_ReturnsNull() =>
        Assert.Null(FeedbackResponseService.ParseAnswer("   "));

    [Theory]
    [InlineData("1", 3, 1)]
    [InlineData("  2  ", 3, 2)]
    [InlineData("3.", 3, 3)]
    [InlineData("99", 3, null)]      // out of range
    [InlineData("0", 3, null)]       // zero invalid
    [InlineData("abc", 3, null)]
    [InlineData("", 3, null)]
    [InlineData("I pick 2", 3, 2)]
    [InlineData("call me at 3 pm", 2, null)]   // 3 out of range with picks=2
    [InlineData("1, 2", 3, 1)]
    public void ParsePickDigit_Cases(string text, int max, int? expected) =>
        Assert.Equal(expected, FeedbackResponseService.ParsePickDigit(text, max));

    // -- Fixture for service-level tests ---------------------------------------

    private readonly Dictionary<Guid, MatchEntity> _matches = [];
    private readonly Dictionary<Guid, List<MatchEntity>> _requestMatches = [];

    private readonly List<MatchFeedback> _added = [];
    private readonly List<Guid> _deleted = [];
    private readonly List<(Guid Id, FeedbackAnswer Answer)> _claimed = [];
    private readonly List<(Guid Id, FeedbackAnswer Answer, DateTimeOffset EtaUtc)> _claimedWithEta = [];
    private readonly List<(PhoneNumber To, string Body)> _sent = [];
    private readonly List<object> _published = [];
    private readonly List<(object Message, TimeSpan Delay)> _scheduled = [];

    private readonly Mock<IFeedbackRepository> _feedbackMock = new();
    private readonly Mock<IMatchRepository> _matchesMock = new();
    private readonly Mock<IConversationAi> _aiMock = new();
    private readonly Mock<IEventBus> _busMock = new();
    private readonly Mock<Wolverine.IMessageBus> _messageBusMock = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly FeedbackOptions _options = new();

    private bool _tryAddResult = true;
    private bool _tryClaimResult = true;
    private ProviderStats? _lastUpsertedStats;

    public FeedbackResponseServiceTests()
    {
        _feedbackMock.Setup(x => x.TryAddPendingAsync(It.IsAny<MatchFeedback>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MatchFeedback f, CancellationToken _) =>
            {
                if (_tryAddResult) _added.Add(f);
                return _tryAddResult;
            });
        _feedbackMock.Setup(x => x.DeletePendingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CancellationToken>((id, _) => _deleted.Add(id))
            .ReturnsAsync(true);
        _feedbackMock.Setup(x => x.TryClaimPendingAsync(
                It.IsAny<Guid>(), It.IsAny<FeedbackAnswer>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, FeedbackAnswer answer, DateTimeOffset _, CancellationToken _) =>
            {
                if (_tryClaimResult) _claimed.Add((id, answer));
                return _tryClaimResult;
            });
        _feedbackMock.Setup(x => x.TryClaimPendingWithEtaAsync(
                It.IsAny<Guid>(), It.IsAny<FeedbackAnswer>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, FeedbackAnswer answer, DateTimeOffset eta, DateTimeOffset _, CancellationToken _) =>
            {
                if (_tryClaimResult) _claimedWithEta.Add((id, answer, eta));
                return _tryClaimResult;
            });
        _feedbackMock.Setup(x => x.UpsertStatsAsync(It.IsAny<ProviderStats>(), It.IsAny<CancellationToken>()))
            .Callback<ProviderStats, CancellationToken>((s, _) => _lastUpsertedStats = s)
            .Returns(Task.CompletedTask);

        _matchesMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _matches.TryGetValue(id, out var m) ? m : null);
        _matchesMock.Setup(x => x.GetForRequestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid requestId, CancellationToken _) =>
                _requestMatches.TryGetValue(requestId, out var l)
                    ? l
                    : Array.Empty<MatchEntity>());
        _matchesMock.Setup(x => x.GetPickedForRequestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid requestId, CancellationToken _) =>
                _requestMatches.TryGetValue(requestId, out var l)
                    ? l.Where(m => m.PickedAt is not null).ToList()
                    : Array.Empty<MatchEntity>());

        // Default AI behavior: Unknown intent, no ETA. Tests override per-input as needed.
        _aiMock.Setup(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentDetectionResult(IntentKind.Unknown, 0.85, "en", "fake"));
        _aiMock.Setup(x => x.DetectIntentAsync("yes", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentDetectionResult(IntentKind.Confirmation, 0.85, "en", "fake"));
        _aiMock.Setup(x => x.DetectIntentAsync("no", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentDetectionResult(IntentKind.Rejection, 0.85, "en", "fake"));
        _aiMock.Setup(x => x.ExtractEtaAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset?)null);

        _busMock.Setup(x => x.PublishAsync(It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(inv => _published.Add(inv.Arguments[0])))
            .Returns(Task.CompletedTask);
        _busMock.Setup(x => x.ScheduleAsync(It.IsAny<It.IsAnyType>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(inv =>
                _scheduled.Add((inv.Arguments[0], (TimeSpan)inv.Arguments[1]))))
            .Returns(Task.CompletedTask);

        _messageBusMock.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextRequested>(), It.IsAny<Wolverine.DeliveryOptions>()))
            .Callback<object, Wolverine.DeliveryOptions>((msg, _) =>
            {
                var req = (SendWhatsAppTextRequested)msg;
                _sent.Add((req.To, req.Text));
            })
            .Returns(ValueTask.CompletedTask);
    }

    private FeedbackResponseService Build() =>
        new(_feedbackMock.Object, _matchesMock.Object, _aiMock.Object, _busMock.Object, _messageBusMock.Object,
            Microsoft.Extensions.Options.Options.Create(_options),
            _clock,
            NullLogger<FeedbackResponseService>.Instance);

    private LazyIntent Intent(string text) => new(_aiMock.Object, text);

    private MatchEntity SeedAnchor(string providerPhone)
    {
        var m = MatchEntity.Create(Guid.NewGuid(), providerPhone, "plumbing", 0, 0, _clock.GetUtcNow());
        _matches[m.Id] = m;
        _requestMatches[m.RequestId] = [m];
        return m;
    }

    private MatchEntity SeedSibling(Guid anchorMatchId, string providerPhone)
    {
        var anchor = _matches[anchorMatchId];
        // ranks higher → first in production order (Score DESC)
        var sibling = MatchEntity.Create(
            anchor.RequestId, providerPhone, anchor.ServiceSlug,
            distanceKm: 0,
            score: anchor.Score + 0.1,
            now: anchor.CreatedAt.AddSeconds(-1));
        sibling.ClaimForPickup(true, _clock.GetUtcNow());
        _matches[sibling.Id] = sibling;
        // Production order: Score DESC -> sibling first, anchor second.
        _requestMatches[anchor.RequestId] = [sibling, anchor];
        return sibling;
    }

    private MatchFeedback SeedPendingForStep(FeedbackStep step, DateTimeOffset? promptedAt = null)
    {
        var anchor = SeedAnchor("+2203331234");
        return MatchFeedback.CreatePending(
            anchor.Id, anchor.RequestId, step, promptedAt ?? _clock.GetUtcNow());
    }

    private static InboundMessage NewInbound(string text) =>
        new(MessageId: "m-" + Guid.NewGuid(),
            From: PhoneNumber.Parse("+2203339999"),
            Timestamp: DateTimeOffset.UtcNow,
            Kind: InboundMessageKind.Text,
            Text: text,
            Location: null,
            RawJson: null);

    // -- HandleAsync: switch fails loudly on unknown FeedbackStep -------------

    [Fact]
    public async Task HandleAsync_UnknownStep_ThrowsInvalidOperation()
    {
        var pending = MatchFeedback.CreatePending(Guid.NewGuid(), Guid.NewGuid(), (FeedbackStep)999, _clock.GetUtcNow());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Build().HandleAsync(NewInbound("yes"), pending, Intent("yes"), CancellationToken.None));
    }

    // -- HandleDidYouFindAsync ------------------------------------------------

    [Fact]
    public async Task DidYouFind_InProgressReply_DoesNotClaimAndAsksRetry()
    {
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);

        await Build().HandleAsync(NewInbound("in progress"), pending, Intent("in progress"), CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Single(_sent);
        Assert.Contains("YES if you found", _sent[0].Body);
    }

    [Fact]
    public async Task DidYouFind_YesSinglePick_PublishesStep2()
    {
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);
        _matches[pending.MatchId].ClaimForPickup(false, DateTimeOffset.UtcNow);

        await Build().HandleAsync(NewInbound("yes"), pending, Intent("yes"), CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Yes, _claimed[0].Answer);
        Assert.Single(_published);
        Assert.Equal(pending.MatchId, ((Step2FeedbackCheck)_published[0]).MatchId);
        Assert.Empty(_scheduled);
    }

    [Fact]
    public async Task DidYouFind_YesMultiPick_ReservesIdentifyWinnerAndSendsPrompt()
    {
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);
        _matches[pending.MatchId].ClaimForPickup(false, DateTimeOffset.UtcNow);
        SeedSibling(pending.MatchId, "+2204445678");

        await Build().HandleAsync(NewInbound("yes"), pending, Intent("yes"), CancellationToken.None);

        Assert.Single(_added);
        Assert.Equal(FeedbackStep.IdentifyWinner, _added[0].Step);
        Assert.Single(_sent);
        Assert.Contains("Which provider", _sent[0].Body);
        Assert.Contains("1)", _sent[0].Body);
        Assert.Contains("2)", _sent[0].Body);
        // Step1 claim happens AFTER the send (so a send-failure leaves Step1 Pending).
        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Yes, _claimed[0].Answer);
        // No Step2 publish — winner identification is still required.
        Assert.Empty(_published);
    }

    [Fact]
    public async Task DidYouFind_YesNoPickedSiblings_LogsAndClaims()
    {
        // PickedAt cleared on anchor between Step1 schedule and reply — claim and exit.
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);
        // Don't set PickedAt — picked.Count == 0

        await Build().HandleAsync(NewInbound("yes"), pending, Intent("yes"), CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Yes, _claimed[0].Answer);
        Assert.Empty(_published);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task DidYouFind_NoReply_ClaimsNoNoPublish()
    {
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);

        await Build().HandleAsync(NewInbound("no"), pending, Intent("no"), CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.No, _claimed[0].Answer);
        Assert.Empty(_published);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task DidYouFind_GarbageWithinRetryWindow_SendsHint()
    {
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind, promptedAt: _clock.GetUtcNow());

        await Build().HandleAsync(NewInbound("xyz"), pending, Intent("xyz"), CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Single(_sent);
        Assert.Contains("Sorry, didn't catch that", _sent[0].Body);
    }

    [Fact]
    public async Task DidYouFind_GarbagePastRetryWindow_SilentNoAiCall()
    {
        var pending = SeedPendingForStep(
            FeedbackStep.DidYouFind,
            promptedAt: _clock.GetUtcNow() - TimeSpan.FromHours(2)); // > ParseRetryWindow

        await Build().HandleAsync(NewInbound("xyz"), pending, Intent("xyz"), CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Empty(_sent);
        // Bound the AI fallback by the retry window — no Ollama call for stale Pending.
        _aiMock.Verify(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DidYouFind_TryClaimRaceLost_NoPublish()
    {
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);
        _matches[pending.MatchId].ClaimForPickup(false, DateTimeOffset.UtcNow);
        _tryClaimResult = false;

        await Build().HandleAsync(NewInbound("yes"), pending, Intent("yes"), CancellationToken.None);

        Assert.Empty(_published);
    }

    // -- HandleIdentifyWinnerAsync --------------------------------------------

    [Fact]
    public async Task IdentifyWinner_ZeroPickedSiblings_Silent()
    {
        var pending = SeedPendingForStep(FeedbackStep.IdentifyWinner);
        // Anchor has no PickedAt — picked.Count == 0

        await Build().HandleAsync(NewInbound("1"), pending, Intent("1"), CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task IdentifyWinner_ValidDigit_ClaimsWinnerSelectedAndPublishesForWinner()
    {
        var pending = SeedPendingForStep(FeedbackStep.IdentifyWinner);
        var anchor = _matches[pending.MatchId];
        anchor.ClaimForPickup(false, DateTimeOffset.UtcNow);
        SeedSibling(pending.MatchId, "+2204445678");
        // Two picked siblings; reply "2" picks the second (anchor).

        await Build().HandleAsync(NewInbound("2"), pending, Intent("2"), CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.WinnerSelected, _claimed[0].Answer);
        Assert.Single(_published);
        // sibling is sorted first (higher Score by default seed), anchor is second.
        var winnerId = ((Step2FeedbackCheck)_published[0]).MatchId;
        Assert.Equal(anchor.Id, winnerId);
    }

    [Fact]
    public async Task IdentifyWinner_FirstSlotDigit_PublishesForHighestScoredSibling()
    {
        var pending = SeedPendingForStep(FeedbackStep.IdentifyWinner);
        _matches[pending.MatchId].ClaimForPickup(false, DateTimeOffset.UtcNow);
        var sibling = SeedSibling(pending.MatchId, "+2204445678");

        await Build().HandleAsync(NewInbound("1"), pending, Intent("1"), CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.WinnerSelected, _claimed[0].Answer);
        Assert.Single(_published);
        // sibling has higher Score per SeedSibling — production order lands it in slot 1.
        Assert.Equal(sibling.Id, ((Step2FeedbackCheck)_published[0]).MatchId);
    }

    [Fact]
    public async Task IdentifyWinner_OutOfRangeWithinRetryWindow_SendsHint()
    {
        var pending = SeedPendingForStep(FeedbackStep.IdentifyWinner, promptedAt: _clock.GetUtcNow());
        _matches[pending.MatchId].ClaimForPickup(false, DateTimeOffset.UtcNow);
        SeedSibling(pending.MatchId, "+2204445678");

        await Build().HandleAsync(NewInbound("99"), pending, Intent("99"), CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Single(_sent);
        Assert.Contains("Reply with the number", _sent[0].Body);
    }

    [Fact]
    public async Task IdentifyWinner_PastRetryWindow_ClaimsSkippedAndLogs()
    {
        var pending = SeedPendingForStep(
            FeedbackStep.IdentifyWinner,
            promptedAt: _clock.GetUtcNow() - TimeSpan.FromHours(2));
        _matches[pending.MatchId].ClaimForPickup(false, DateTimeOffset.UtcNow);
        SeedSibling(pending.MatchId, "+2204445678");

        await Build().HandleAsync(NewInbound("xyz"), pending, Intent("xyz"), CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Empty(_sent);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task IdentifyWinner_TryClaimRaceLost_NoPublish()
    {
        var pending = SeedPendingForStep(FeedbackStep.IdentifyWinner);
        _matches[pending.MatchId].ClaimForPickup(false, DateTimeOffset.UtcNow);
        SeedSibling(pending.MatchId, "+2204445678");
        _tryClaimResult = false;

        await Build().HandleAsync(NewInbound("1"), pending, Intent("1"), CancellationToken.None);

        Assert.Empty(_published);
    }

    // -- HandleJobCompletedAsync ----------------------------------------------

    [Fact]
    public async Task JobCompleted_Yes_RecordsOutcomeSuccess()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);

        await Build().HandleAsync(NewInbound("yes"), pending, Intent("yes"), CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Yes, _claimed[0].Answer);
        Assert.NotNull(_lastUpsertedStats);
        Assert.Equal(1, _lastUpsertedStats!.SuccessCount);
    }

    [Fact]
    public async Task JobCompleted_No_RecordsOutcomeFailure()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);

        await Build().HandleAsync(NewInbound("no"), pending, Intent("no"), CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.No, _claimed[0].Answer);
        Assert.NotNull(_lastUpsertedStats);
        Assert.Equal(0, _lastUpsertedStats!.SuccessCount);
        Assert.Equal(1, _lastUpsertedStats!.CompletedCount);
    }

    [Fact]
    public async Task JobCompleted_InProgress_ReservesAwaitingEtaAndAsks()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);

        await Build().HandleAsync(NewInbound("in progress"), pending, Intent("in progress"), CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.InProgress, _claimed[0].Answer);
        Assert.Single(_added);
        Assert.Equal(FeedbackStep.AwaitingEta, _added[0].Step);
        Assert.Single(_sent);
        Assert.Contains("when do you think", _sent[0].Body);
    }

    [Fact]
    public async Task JobCompleted_GarbagePastRetryWindow_SilentNoAiCall()
    {
        var pending = SeedPendingForStep(
            FeedbackStep.JobCompleted,
            promptedAt: _clock.GetUtcNow() - TimeSpan.FromHours(2));

        await Build().HandleAsync(NewInbound("xyz"), pending, Intent("xyz"), CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Empty(_sent);
        _aiMock.Verify(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- HandleAwaitingEtaAsync -----------------------------------------------

    [Fact]
    public async Task AwaitingEta_ValidFutureEta_ClaimsEtaCapturedAndSchedules()
    {
        var pending = SeedPendingForStep(FeedbackStep.AwaitingEta);
        var now = _clock.GetUtcNow();
        var expectedEta = now + TimeSpan.FromHours(3);
        // Heuristic: "in 3 hours" → now + 3h.
        _aiMock.Setup(x => x.ExtractEtaAsync("in 3 hours", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, DateTimeOffset n, CancellationToken _) => n + TimeSpan.FromHours(3));

        await Build().HandleAsync(NewInbound("in 3 hours"), pending, Intent("in 3 hours"), CancellationToken.None);

        Assert.Single(_claimedWithEta);
        var claim = _claimedWithEta[0];
        Assert.Equal(FeedbackAnswer.EtaCaptured, claim.Answer);
        Assert.Equal(expectedEta, claim.EtaUtc);
        Assert.Single(_scheduled);
        var (schedMsg, schedDelay) = _scheduled[0];
        Assert.Equal(pending.MatchId, ((Step2FeedbackCheck)schedMsg).MatchId);
        Assert.True(schedDelay >= TimeSpan.FromHours(3));
    }

    [Fact]
    public async Task AwaitingEta_AiNullWithinRetryWindow_SendsHint()
    {
        var pending = SeedPendingForStep(FeedbackStep.AwaitingEta);
        // Override "in 3 hours" → null (default already returns null, but make explicit).
        _aiMock.Setup(x => x.ExtractEtaAsync("in 3 hours", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset?)null);

        await Build().HandleAsync(NewInbound("in 3 hours"), pending, Intent("in 3 hours"), CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Single(_sent);
        Assert.Contains("when do you think", _sent[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AwaitingEta_PastRetryWindow_ClaimsSkippedAndSchedulesFallback()
    {
        var pending = SeedPendingForStep(
            FeedbackStep.AwaitingEta,
            promptedAt: _clock.GetUtcNow() - TimeSpan.FromHours(2));
        // "xyz" is short — LooksLikeEtaCandidate rejects pre-AI. Default ETA mock returns null.

        await Build().HandleAsync(NewInbound("xyz"), pending, Intent("xyz"), CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Single(_scheduled);
        var (_, delay) = _scheduled[0];
        Assert.Equal(_options.Step2InProgressRecheckDelay, delay);
    }

    [Fact]
    public async Task AwaitingEta_AiReturnsBeyondHorizon_FallsBackAsSkipped()
    {
        var pending = SeedPendingForStep(FeedbackStep.AwaitingEta);
        // Heuristic: "in 99 days" → now + 99d (> MaxEtaHorizon=7d).
        _aiMock.Setup(x => x.ExtractEtaAsync("in 99 days", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, DateTimeOffset n, CancellationToken _) => n + TimeSpan.FromDays(99));

        await Build().HandleAsync(NewInbound("in 99 days"), pending, Intent("in 99 days"), CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Single(_scheduled);
        var (_, delay) = _scheduled[0];
        Assert.Equal(_options.Step2InProgressRecheckDelay, delay);
    }

    [Fact]
    public async Task AwaitingEta_ShortGarbageReply_SkipsAiCallButSendsHint()
    {
        // E9 precheck: short reply with no digits and no ETA keyword should never
        // hit Ollama — saves a round trip on hostile input. The handler still owes
        // the client a retry hint so the conversation does not silently stall.
        var pending = SeedPendingForStep(FeedbackStep.AwaitingEta);

        await Build().HandleAsync(NewInbound("ok"), pending, Intent("ok"), CancellationToken.None);

        _aiMock.Verify(x => x.ExtractEtaAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(_sent);
        Assert.Contains("when do you think", _sent[0].Body, StringComparison.OrdinalIgnoreCase);
    }
}
