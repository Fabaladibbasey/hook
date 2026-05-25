using Hook.Features.Feedback;
using Hook.Features.Feedback.AggregateStats;
using Hook.Features.Feedback.Eta;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Features.Feedback.Step1Intent;
using Hook.Features.Feedback.Step1Prompt;
using Hook.Features.Feedback.Step2Intent;
using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Core;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;
using ServiceRequestEntity = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest;

namespace Hook.UnitTests.Feedback;

public class FeedbackResponseServiceTests
{
    [Theory]
    [InlineData(30, "30 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(89, "1h 29m")]
    [InlineData(90, "1h 30m")]
    [InlineData(360, "6 hours")]
    public void Humanize_FormatsByGranularity(int totalMinutes, string expected) =>
        Assert.Equal(expected, FeedbackResponseService.Humanize(TimeSpan.FromMinutes(totalMinutes)));

    [Theory]
    [InlineData("call +220 700 12345 if you want", "call [phone] if you want")]
    [InlineData("220-700-12345 is my number", "[phone] is my number")]
    [InlineData("(220) 700 1234", "[phone]")]
    [InlineData("+220.700.12345 maybe later", "[phone] maybe later")]
    [InlineData("pure digits +22070012345", "pure digits [phone]")]
    [InlineData("short 1234567 stays", "short 1234567 stays")]
    public void ScrubForOutbox_replaces_separator_formatted_phone_numbers(string input, string expected) =>
        Assert.Equal(expected, FeedbackResponseService.ScrubForOutbox(input, 1000));

    [Fact]
    public void ScrubForOutbox_OrderNumber_EmbeddedInWord_NotScrubbed()
    {
        // Word-boundary anchor must suppress matches inside alphanumeric IDs —
        // before the \b tightening this would have been mangled to "your [phone] ships".
        Assert.Equal(
            "your ORD12345678 ships",
            FeedbackResponseService.ScrubForOutbox("your ORD12345678 ships", 1000));
    }

    [Fact]
    public void ScrubForOutbox_RealPhone_StillScrubbed()
    {
        Assert.Equal(
            "ping +2207001234567 ack",
            FeedbackResponseService.ScrubForOutbox("ping +2207001234567 ack", 1000)
                .Replace("[phone]", "+2207001234567"));
        // Round-trip via Replace asserts the original phone was actually scrubbed
        // to [phone] and back, distinguishing the no-op case.
        Assert.Equal(
            "ping [phone] ack",
            FeedbackResponseService.ScrubForOutbox("ping +2207001234567 ack", 1000));
    }

    [Fact]
    public void ScrubForOutbox_TruncatesBeforeScrub()
    {
        // Truncation runs first so backtracking is bounded on adversarial input.
        // A 20-char cap on a 200-char input ending in a phone must drop the phone
        // entirely (rather than slicing inside a [phone] token mid-replacement).
        var input = new string('a', 100) + " +2207001234567";
        var result = FeedbackResponseService.ScrubForOutbox(input, 20);
        Assert.Equal(20, result.Length);
        Assert.DoesNotContain("[phone]", result);
    }

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
    private readonly List<Guid> _rescheduled = [];
    private readonly List<(PhoneNumber To, string Body)> _sent = [];
    private readonly List<object> _published = [];
    private readonly List<(object Message, TimeSpan Delay)> _scheduled = [];
    private readonly List<ExtractEtaCommand> _extractEtaRequests = [];
    private readonly List<ExtractStep1IntentCommand> _extractStep1Requests = [];
    private readonly List<ExtractStep2IntentCommand> _extractStep2Requests = [];

    private readonly Dictionary<Guid, MatchFeedback> _store = [];
    private readonly Dictionary<Guid, int> _storeBaselineRecheck = [];

    private readonly Mock<IFeedbackRepository> _feedbackMock = new();
    private readonly Mock<IMatchRepository> _matchesMock = new();
    private readonly Mock<IServiceRequestRepository> _requestsMock = new();
    private readonly Mock<IEventBus> _busMock = new();
    private readonly Mock<Wolverine.IMessageBus> _messageBusMock = new();
    private readonly Dictionary<Guid, ServiceRequestEntity> _requests = [];
    // Hard-coded test dates rot; derive from wall-clock so the relative offsets stay meaningful.
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
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
        _feedbackMock.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _store.TryGetValue(id, out var v) ? v : null);
        _feedbackMock.Setup(x => x.TrySaveAsync(It.IsAny<MatchFeedback>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MatchFeedback agg, CancellationToken _) =>
            {
                if (!_tryClaimResult) return false;
                if (agg.Answer is FeedbackAnswer.EtaCaptured && agg.EtaUtc is { } eta)
                    _claimedWithEta.Add((agg.Id, agg.Answer, eta));
                else if (agg.Answer is not FeedbackAnswer.Pending)
                    _claimed.Add((agg.Id, agg.Answer));
                else if (_storeBaselineRecheck.TryGetValue(agg.Id, out var baseline) && agg.Step1RecheckCount > baseline)
                {
                    _rescheduled.Add(agg.Id);
                    _storeBaselineRecheck[agg.Id] = agg.Step1RecheckCount;
                }
                return true;
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

        _requestsMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _requests.TryGetValue(id, out var r) ? r : null);

        _busMock.Setup(x => x.PublishAsync(It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(inv => _published.Add(inv.Arguments[0])))
            .Returns(Task.CompletedTask);
        _busMock.Setup(x => x.ScheduleAsync(It.IsAny<It.IsAnyType>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(inv =>
                _scheduled.Add((inv.Arguments[0], (TimeSpan)inv.Arguments[1]))))
            .Returns(Task.CompletedTask);

        _messageBusMock.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextCommand>(), It.IsAny<Wolverine.DeliveryOptions>()))
            .Callback<object, Wolverine.DeliveryOptions>((msg, _) =>
            {
                var req = (SendWhatsAppTextCommand)msg;
                _sent.Add((req.To, req.Text));
            })
            .Returns(ValueTask.CompletedTask);
        _messageBusMock.Setup(x => x.PublishAsync(It.IsAny<ExtractEtaCommand>(), It.IsAny<Wolverine.DeliveryOptions>()))
            .Callback<object, Wolverine.DeliveryOptions>((msg, _) => _extractEtaRequests.Add((ExtractEtaCommand)msg))
            .Returns(ValueTask.CompletedTask);
        _messageBusMock.Setup(x => x.PublishAsync(It.IsAny<ExtractStep1IntentCommand>(), It.IsAny<Wolverine.DeliveryOptions>()))
            .Callback<object, Wolverine.DeliveryOptions>((msg, _) =>
                _extractStep1Requests.Add((ExtractStep1IntentCommand)msg))
            .Returns(ValueTask.CompletedTask);
        _messageBusMock.Setup(x => x.PublishAsync(It.IsAny<ExtractStep2IntentCommand>(), It.IsAny<Wolverine.DeliveryOptions>()))
            .Callback<object, Wolverine.DeliveryOptions>((msg, _) =>
                _extractStep2Requests.Add((ExtractStep2IntentCommand)msg))
            .Returns(ValueTask.CompletedTask);
    }

    private FeedbackResponseService Build() =>
        new(_feedbackMock.Object, _matchesMock.Object, _requestsMock.Object,
            _busMock.Object, _messageBusMock.Object,
            Microsoft.Extensions.Options.Options.Create(_options),
            _clock,
            NullLogger<FeedbackResponseService>.Instance);

    private MatchEntity SeedAnchor(string providerPhone)
    {
        // Create the ServiceRequest first so its Id can pin the match's RequestId
        // — the recheck-command builder loads request via match.RequestId.
        var req = ServiceRequestEntity.Create(
            clientPhone: "+2203339999",
            serviceSlug: "plumbing",
            location: new Location(13.45, -16.6),
            formattedAddress: "Banjul",
            description: "req-test",
            initialRadiusKm: 5.0,
            now: _clock.GetUtcNow(),
            sharePhoneNumber: false);
        var m = MatchEntity.Create(req.Id, providerPhone, "plumbing", 0, 0, _clock.GetUtcNow());
        _matches[m.Id] = m;
        _requestMatches[m.RequestId] = [m];
        _requests[req.Id] = req;
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
        var p = MatchFeedback.CreatePending(
            anchor.Id, anchor.RequestId, step, promptedAt ?? _clock.GetUtcNow());
        _store[p.Id] = p;
        _storeBaselineRecheck[p.Id] = p.Step1RecheckCount;
        return p;
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
        // HandleAsync re-loads via GetByIdAsync now — seed the store so the
        // switch runs and the unknown-step throw fires.
        _store[pending.Id] = pending;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Build().HandleAsync(NewInbound("yes"), pending, CancellationToken.None));
    }

    // -- HandleDidYouFindAsync ------------------------------------------------

    [Fact]
    public async Task DidYouFind_AmbiguousReply_DefersToAiExtract()
    {
        // "in progress" is not Yes/No/Stop/Reschedule; falls through to the AI
        // extractor (outbox-driven) without claiming or sending inline.
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);

        await Build().HandleAsync(NewInbound("in progress"), pending, CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Empty(_sent);
        Assert.Single(_extractStep1Requests);
        Assert.Equal(pending.Id, _extractStep1Requests[0].PendingId);
        Assert.Equal("in progress", _extractStep1Requests[0].Text);
    }

    [Fact]
    public async Task DidYouFind_YesSinglePick_PublishesStep2()
    {
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);
        _matches[pending.MatchId].ClaimForPickup(false, DateTimeOffset.UtcNow);

        await Build().HandleAsync(NewInbound("yes"), pending, CancellationToken.None);

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

        await Build().HandleAsync(NewInbound("yes"), pending, CancellationToken.None);

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

        await Build().HandleAsync(NewInbound("yes"), pending, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Yes, _claimed[0].Answer);
        Assert.Empty(_published);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task DidYouFind_NoReply_ClaimsNoAndOpensCaptureNoReasonFollowUp()
    {
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);

        await Build().HandleAsync(NewInbound("no"), pending, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.No, _claimed[0].Answer);
        Assert.Single(_added);
        Assert.Equal(FeedbackStep.CaptureNoReason, _added[0].Step);
        Assert.Empty(_published);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.Step1NoAsk, _sent[0].Body);
    }

    [Fact]
    public async Task DidYouFind_NoReply_RaceLost_DoesNotAck()
    {
        // Reserve-then-publish-then-claim: a unique-collision on the CaptureNoReason
        // partial index (prior run already reserved + sent the prompt) must short-circuit
        // silently — no duplicate prompt, no Step1 claim (claim follows reserve, so the
        // original DidYouFind row stays open for legitimate retry).
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);
        _tryAddResult = false;

        await Build().HandleAsync(NewInbound("no"), pending, CancellationToken.None);

        Assert.Empty(_added);
        Assert.Empty(_sent);
        Assert.Empty(_claimed);
    }

    [Fact]
    public async Task DidYouFind_GarbageWithinRetryWindow_PublishesExtractStep1Intent()
    {
        // Layered parser misses → AI fallback. Inline send happens in ApplyStep1IntentHandler
        // once the AI returns Unclear (see ApplyStep1IntentAsync_Unclear_WithinWindow_SendsHint).
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind, promptedAt: _clock.GetUtcNow());

        await Build().HandleAsync(NewInbound("xyz"), pending, CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Empty(_sent);
        Assert.Single(_extractStep1Requests);
    }

    [Fact]
    public async Task DidYouFind_GarbagePastRetryWindow_ClaimsSkippedAndAcks()
    {
        var pending = SeedPendingForStep(
            FeedbackStep.DidYouFind,
            promptedAt: _clock.GetUtcNow() - TimeSpan.FromHours(2)); // > ParseRetryWindow

        await Build().HandleAsync(NewInbound("xyz"), pending, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.SkippedAck, _sent[0].Body);
    }

    [Fact]
    public async Task DidYouFind_TryClaimRaceLost_NoPublish()
    {
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);
        _matches[pending.MatchId].ClaimForPickup(false, DateTimeOffset.UtcNow);
        _tryClaimResult = false;

        await Build().HandleAsync(NewInbound("yes"), pending, CancellationToken.None);

        Assert.Empty(_published);
    }

    // -- New layered-parser branches: Stop / Reschedule / Apply ---------------

    [Fact]
    public async Task DidYouFind_StopReply_ClaimsSkippedSendsStopAck_NoExtract()
    {
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);

        await Build().HandleAsync(NewInbound("stop asking me"), pending, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.StopAck, _sent[0].Body);
        Assert.Empty(_extractStep1Requests);
    }

    [Fact]
    public async Task DidYouFind_BareStopToken_RoutesToStopBeforeRejection()
    {
        // Without the StopAsking-precedes-Rejection guard, QuickIntent.Detect("stop")
        // would return Rejection → claim No + send NoReason follow-up.
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);

        await Build().HandleAsync(NewInbound("stop"), pending, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Equal(FeedbackCopy.StopAck, _sent[0].Body);
    }

    [Fact]
    public async Task DidYouFind_RescheduleReply_BumpsRecheckSchedulesAtFirstRung()
    {
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);

        await Build().HandleAsync(NewInbound("still looking"), pending, CancellationToken.None);

        Assert.Equal(pending.Id, _rescheduled.Single());
        Assert.Equal(1, pending.Step1RecheckCount);
        Assert.Single(_scheduled);
        var (msg, delay) = _scheduled[0];
        Assert.IsType<Step1RecheckCommand>(msg);
        Assert.Equal(((Step1RecheckCommand)msg).MatchId, pending.MatchId);
        Assert.Equal(_options.Step1RecheckSchedule[0], delay);
        Assert.Single(_sent);
        Assert.Contains("check back", _sent[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DidYouFind_RescheduleAtLadderCap_ClaimsSkippedSilently()
    {
        // Pending row already at the cap; the next ambiguous reschedule must
        // claim Skipped with no extra prompts.
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);
        // Step1MaxRechecks=0 → the very next no-eta reschedule claims Skipped.
        var maxedOpts = new FeedbackOptions { Step1MaxRechecks = 0 };
        var svc = new FeedbackResponseService(
            _feedbackMock.Object, _matchesMock.Object, _requestsMock.Object,
            _busMock.Object, _messageBusMock.Object,
            Microsoft.Extensions.Options.Options.Create(maxedOpts),
            _clock,
            NullLogger<FeedbackResponseService>.Instance);

        await svc.HandleAsync(NewInbound("still looking"), pending, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Empty(_scheduled);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task ApplyStep1IntentAsync_RescheduleWithEta_SchedulesAtEtaPlusBuffer()
    {
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);

        var eta = _clock.GetUtcNow().AddHours(3);
        await Build().ApplyStep1IntentAsync(
            pending, PhoneNumber.Parse("+2203339999"),
            Step1ReplyIntent.Reschedule, eta, CancellationToken.None);

        Assert.Single(_scheduled);
        var (msg, delay) = _scheduled[0];
        Assert.IsType<Step1RecheckCommand>(msg);
        // delay ≈ 3h + EtaScheduleBuffer (5m by default).
        var expected = TimeSpan.FromHours(3) + _options.EtaScheduleBuffer;
        Assert.True(Math.Abs((delay - expected).TotalMilliseconds) < 5);
    }

    [Fact]
    public async Task ApplyStep1IntentAsync_RescheduleWithPastHorizonEta_CapsAtHorizon()
    {
        // 10 days out > MaxEtaHorizon (7d default) — we cap rather than StopAsking
        // so the user who says "ask in a month" still gets one bounded recheck.
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind);
        var eta = _clock.GetUtcNow().AddDays(10);

        await Build().ApplyStep1IntentAsync(
            pending, PhoneNumber.Parse("+2203339999"),
            Step1ReplyIntent.Reschedule, eta, CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Single(_scheduled);
        var (msg, delay) = _scheduled[0];
        Assert.IsType<Step1RecheckCommand>(msg);
        var expected = _options.MaxEtaHorizon + _options.EtaScheduleBuffer;
        Assert.Equal(expected, delay);
        Assert.Single(_sent);
        Assert.Contains("check back", _sent[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyStep1IntentAsync_Unclear_WithinWindow_SendsRetryHint()
    {
        var pending = SeedPendingForStep(FeedbackStep.DidYouFind, promptedAt: _clock.GetUtcNow());

        await Build().ApplyStep1IntentAsync(
            pending, PhoneNumber.Parse("+2203339999"),
            Step1ReplyIntent.Unclear, null, CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.Step1UnclearRetry, _sent[0].Body);
    }

    [Fact]
    public async Task HandleCaptureNoReasonAsync_FreeText_PersistsAndAcks()
    {
        var pending = SeedPendingForStep(FeedbackStep.CaptureNoReason);
        await Build().HandleAsync(NewInbound("prices were too high"), pending, CancellationToken.None);

        Assert.Equal(FeedbackAnswer.NoReasonCaptured, pending.Answer);
        Assert.Equal("prices were too high", pending.NoReason);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.CaptureNoReasonAck, _sent[0].Body);
    }

    [Fact]
    public async Task HandleCaptureNoReasonAsync_Skip_PersistsNullReason()
    {
        var pending = SeedPendingForStep(FeedbackStep.CaptureNoReason);
        await Build().HandleAsync(NewInbound("SKIP"), pending, CancellationToken.None);

        Assert.Equal(FeedbackAnswer.NoReasonCaptured, pending.Answer);
        Assert.Null(pending.NoReason);
        Assert.Single(_sent);
    }

    // -- HandleIdentifyWinnerAsync --------------------------------------------

    [Fact]
    public async Task IdentifyWinner_ZeroPickedSiblings_Silent()
    {
        var pending = SeedPendingForStep(FeedbackStep.IdentifyWinner);
        // Anchor has no PickedAt — picked.Count == 0

        await Build().HandleAsync(NewInbound("1"), pending, CancellationToken.None);

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

        await Build().HandleAsync(NewInbound("2"), pending, CancellationToken.None);

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

        await Build().HandleAsync(NewInbound("1"), pending, CancellationToken.None);

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

        await Build().HandleAsync(NewInbound("99"), pending, CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Single(_sent);
        Assert.Contains("Reply with the number", _sent[0].Body);
    }

    [Fact]
    public async Task IdentifyWinner_PastRetryWindow_ClaimsSkippedAndAcks()
    {
        var pending = SeedPendingForStep(
            FeedbackStep.IdentifyWinner,
            promptedAt: _clock.GetUtcNow() - TimeSpan.FromHours(2));
        _matches[pending.MatchId].ClaimForPickup(false, DateTimeOffset.UtcNow);
        SeedSibling(pending.MatchId, "+2204445678");

        await Build().HandleAsync(NewInbound("xyz"), pending, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.SkippedAck, _sent[0].Body);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task IdentifyWinner_TryClaimRaceLost_NoPublish()
    {
        var pending = SeedPendingForStep(FeedbackStep.IdentifyWinner);
        _matches[pending.MatchId].ClaimForPickup(false, DateTimeOffset.UtcNow);
        SeedSibling(pending.MatchId, "+2204445678");
        _tryClaimResult = false;

        await Build().HandleAsync(NewInbound("1"), pending, CancellationToken.None);

        Assert.Empty(_published);
    }

    // -- HandleJobCompletedAsync ----------------------------------------------

    [Fact]
    public async Task JobCompleted_Yes_RecordsOutcomeSuccessAndAcks()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);

        await Build().HandleAsync(NewInbound("yes"), pending, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Yes, _claimed[0].Answer);
        Assert.NotNull(_lastUpsertedStats);
        Assert.Equal(1, _lastUpsertedStats!.SuccessCount);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.Step2YesAck, _sent[0].Body);
    }

    [Fact]
    public async Task JobCompleted_No_RecordsOutcomeFailureAndAcks()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);

        await Build().HandleAsync(NewInbound("no"), pending, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.No, _claimed[0].Answer);
        Assert.NotNull(_lastUpsertedStats);
        Assert.Equal(0, _lastUpsertedStats!.SuccessCount);
        Assert.Equal(1, _lastUpsertedStats!.CompletedCount);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.Step2NoAck, _sent[0].Body);
    }

    [Fact]
    public async Task JobCompleted_InProgress_ReservesAwaitingEtaAndAsks()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);

        await Build().HandleAsync(NewInbound("in progress"), pending, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.InProgress, _claimed[0].Answer);
        Assert.Single(_added);
        Assert.Equal(FeedbackStep.AwaitingEta, _added[0].Step);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.Step2AwaitingEtaAsk, _sent[0].Body);
    }

    [Fact]
    public async Task JobCompleted_GarbagePastRetryWindow_ClaimsSkippedAndAcks()
    {
        var pending = SeedPendingForStep(
            FeedbackStep.JobCompleted,
            promptedAt: _clock.GetUtcNow() - TimeSpan.FromHours(2));

        await Build().HandleAsync(NewInbound("xyz"), pending, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.SkippedAck, _sent[0].Body);
    }

    // -- HandleAwaitingEtaAsync -----------------------------------------------
    // ETA-extraction outcomes moved to ApplyEtaHandler — see that handler's
    // tests for valid/horizon/null branches. These tests assert the orchestrator
    // defers candidate input to the outbox and handles non-candidate inline.

    [Fact]
    public async Task AwaitingEta_CandidateInput_PublishesExtractRequestAndAck()
    {
        var pending = SeedPendingForStep(FeedbackStep.AwaitingEta);

        await Build().HandleAsync(NewInbound("in 3 hours"), pending, CancellationToken.None);

        Assert.Single(_extractEtaRequests);
        Assert.Equal(pending.Id, _extractEtaRequests[0].PendingId);
        Assert.Equal("in 3 hours", _extractEtaRequests[0].Text);
        Assert.Contains(_sent, s => s.Body == FeedbackCopy.AwaitingEtaGotIt);
    }

    [Fact]
    public async Task AwaitingEta_PastRetryWindow_NonCandidate_ClaimsSkippedAndSchedulesFallback()
    {
        var pending = SeedPendingForStep(
            FeedbackStep.AwaitingEta,
            promptedAt: _clock.GetUtcNow() - TimeSpan.FromHours(2));

        await Build().HandleAsync(NewInbound("xyz"), pending, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Single(_scheduled);
        var (_, delay) = _scheduled[0];
        Assert.Equal(_options.Step2InProgressRecheckDelay, delay);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.SkippedAck, _sent[0].Body);
    }

    [Fact]
    public async Task AwaitingEta_ShortGarbageReply_SkipsExtractAndSendsHint()
    {
        // Short reply with no digits and no ETA keyword bypasses Ollama entirely.
        var pending = SeedPendingForStep(FeedbackStep.AwaitingEta);

        await Build().HandleAsync(NewInbound("ok"), pending, CancellationToken.None);

        Assert.Empty(_extractEtaRequests);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.AwaitingEtaRetry, _sent[0].Body);
    }

    // -- HandleJobCompletedAsync: layered parser + AI fallback ----------------

    [Fact]
    public async Task JobCompleted_DeterministicInNHours_SchedulesDirectly_NoAwaitingEta()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);

        await Build().HandleAsync(NewInbound("in 3 hours"), pending, CancellationToken.None);

        Assert.Empty(_extractStep2Requests);
        Assert.Empty(_claimed);
        Assert.Single(_claimedWithEta);
        Assert.Equal(FeedbackAnswer.EtaCaptured, _claimedWithEta[0].Answer);
        Assert.Single(_scheduled);
        Assert.Empty(_added);
        Assert.Single(_sent);
        Assert.Contains("check back", _sent[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JobCompleted_DeterministicNotYet_RoutesToApplyInProgress()
    {
        // "not yet" matches QuickIntent.DetectInProgress (via DetectReschedule); the
        // layered parser short-circuits to ApplyStep2IntentAsync(InProgress, eta:null)
        // which reserves the AwaitingEta row + asks for an ETA.
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);

        await Build().HandleAsync(NewInbound("not yet"), pending, CancellationToken.None);

        Assert.Empty(_extractStep2Requests);
        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.InProgress, _claimed[0].Answer);
        Assert.Single(_added);
        Assert.Equal(FeedbackStep.AwaitingEta, _added[0].Step);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.Step2AwaitingEtaAsk, _sent[0].Body);
    }

    [Fact]
    public async Task JobCompleted_AiMissPath_ScrubsPhoneAndCapsText()
    {
        // Use a non-deterministic-classified phrase: phone + padding past OutboxTextMaxChars,
        // but no in-progress/yes/no/stop tokens that would short-circuit before the publish.
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted, promptedAt: _clock.GetUtcNow());
        var noise = "plz call +2207771234 once you finish " + new string('x', 2000);

        await Build().HandleAsync(NewInbound(noise), pending, CancellationToken.None);

        Assert.Single(_extractStep2Requests);
        var sent = _extractStep2Requests[0].Text;
        Assert.DoesNotContain("+2207771234", sent);
        Assert.Contains("[phone]", sent);
        Assert.True(sent.Length <= _options.OutboxTextMaxChars,
            $"Expected ≤{_options.OutboxTextMaxChars} chars, got {sent.Length}");
    }

    [Fact]
    public async Task JobCompleted_AiMissPath_PublishesExtractStep2IntentCommand()
    {
        // Garbage within ParseRetryWindow: deterministic miss → AI extractor.
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted, promptedAt: _clock.GetUtcNow());

        await Build().HandleAsync(NewInbound("blegh"), pending, CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Empty(_sent);
        Assert.Single(_extractStep2Requests);
        Assert.Equal(pending.Id, _extractStep2Requests[0].PendingId);
        Assert.Equal(pending.MatchId, _extractStep2Requests[0].MatchId);
        Assert.Equal("blegh", _extractStep2Requests[0].Text);
    }

    [Fact]
    public async Task JobCompleted_AiMissPath_StaleWindow_ClaimsSkippedAndAcks()
    {
        var pending = SeedPendingForStep(
            FeedbackStep.JobCompleted,
            promptedAt: _clock.GetUtcNow() - TimeSpan.FromHours(2));

        await Build().HandleAsync(NewInbound("blegh"), pending, CancellationToken.None);

        Assert.Empty(_extractStep2Requests);
        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.SkippedAck, _sent[0].Body);
    }

    [Fact]
    public async Task JobCompleted_StopReply_ClaimsSkippedAndSendsStopAck()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);

        await Build().HandleAsync(NewInbound("stop asking me"), pending, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.StopAck, _sent[0].Body);
        Assert.Empty(_extractStep2Requests);
    }

    [Fact]
    public async Task JobCompleted_BareStopToken_RoutesToStopBeforeRejection()
    {
        // Bare "stop" would map to Rejection under QuickIntent.Detect; the
        // StopAsking-precedes-Rejection guard routes it to Skipped instead.
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);

        await Build().HandleAsync(NewInbound("stop"), pending, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Equal(FeedbackCopy.StopAck, _sent[0].Body);
    }

    // -- ApplyStep2IntentAsync: per-branch coverage ---------------------------

    [Fact]
    public async Task ApplyStep2IntentAsync_Yes_RecordsOutcomeSuccessAndAcks()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);

        await Build().ApplyStep2IntentAsync(
            pending, PhoneNumber.Parse("+2203339999"),
            Step2ReplyIntent.Yes, null, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Yes, _claimed[0].Answer);
        Assert.NotNull(_lastUpsertedStats);
        Assert.Equal(1, _lastUpsertedStats!.SuccessCount);
        Assert.Equal(FeedbackCopy.Step2YesAck, _sent[0].Body);
    }

    [Fact]
    public async Task ApplyStep2IntentAsync_No_RecordsOutcomeFailureAndAcks()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);

        await Build().ApplyStep2IntentAsync(
            pending, PhoneNumber.Parse("+2203339999"),
            Step2ReplyIntent.No, null, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.No, _claimed[0].Answer);
        Assert.NotNull(_lastUpsertedStats);
        Assert.Equal(0, _lastUpsertedStats!.SuccessCount);
        Assert.Equal(1, _lastUpsertedStats!.CompletedCount);
        Assert.Equal(FeedbackCopy.Step2NoAck, _sent[0].Body);
    }

    [Fact]
    public async Task ApplyStep2IntentAsync_InProgressNoEta_ReservesAwaitingEtaAndAsks()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);

        await Build().ApplyStep2IntentAsync(
            pending, PhoneNumber.Parse("+2203339999"),
            Step2ReplyIntent.InProgress, null, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.InProgress, _claimed[0].Answer);
        Assert.Single(_added);
        Assert.Equal(FeedbackStep.AwaitingEta, _added[0].Step);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.Step2AwaitingEtaAsk, _sent[0].Body);
        Assert.Empty(_scheduled);
    }

    [Fact]
    public async Task ApplyStep2IntentAsync_InProgressWithEta_SchedulesStep2AtEtaPlusBuffer()
    {
        // Single-shot InProgress+ETA — skip AwaitingEta entirely, persist ETA + schedule Step2 directly.
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);
        var eta = _clock.GetUtcNow().AddHours(3);

        await Build().ApplyStep2IntentAsync(
            pending, PhoneNumber.Parse("+2203339999"),
            Step2ReplyIntent.InProgress, eta, CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Single(_claimedWithEta);
        Assert.Equal(FeedbackAnswer.EtaCaptured, _claimedWithEta[0].Answer);
        Assert.True(Math.Abs((_claimedWithEta[0].EtaUtc - eta).TotalMilliseconds) < 5);
        Assert.Empty(_added);
        Assert.Single(_scheduled);
        var (msg, delay) = _scheduled[0];
        Assert.IsType<Step2FeedbackCheck>(msg);
        var expected = TimeSpan.FromHours(3) + _options.EtaScheduleBuffer;
        Assert.True(Math.Abs((delay - expected).TotalMilliseconds) < 5);
        Assert.Single(_sent);
        Assert.Contains("check back", _sent[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyStep2IntentAsync_InProgressWithBeyondHorizonEta_CapsAtHorizonAndSchedules()
    {
        // 10d > MaxEtaHorizon (7d default). Mirrors Step1 Reschedule cap policy:
        // user said "ask in a month" → treat as intent to defer, commit to one bounded recheck.
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);
        var eta = _clock.GetUtcNow().AddDays(10);

        await Build().ApplyStep2IntentAsync(
            pending, PhoneNumber.Parse("+2203339999"),
            Step2ReplyIntent.InProgress, eta, CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Single(_claimedWithEta);
        Assert.Equal(FeedbackAnswer.EtaCaptured, _claimedWithEta[0].Answer);
        Assert.Empty(_added);
        Assert.Single(_scheduled);
        var (_, delay) = _scheduled[0];
        var expected = _options.MaxEtaHorizon + _options.EtaScheduleBuffer;
        Assert.Equal(expected, delay);
        Assert.Single(_sent);
        Assert.Contains("check back", _sent[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyStep2IntentAsync_StopAsking_ClaimsSkippedAndAcks()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);

        await Build().ApplyStep2IntentAsync(
            pending, PhoneNumber.Parse("+2203339999"),
            Step2ReplyIntent.StopAsking, null, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.StopAck, _sent[0].Body);
    }

    [Fact]
    public async Task ApplyStep2IntentAsync_Unclear_WithinWindow_SendsRetryHint()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted, promptedAt: _clock.GetUtcNow());

        await Build().ApplyStep2IntentAsync(
            pending, PhoneNumber.Parse("+2203339999"),
            Step2ReplyIntent.Unclear, null, CancellationToken.None);

        Assert.Empty(_claimed);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.Step2UnclearRetry, _sent[0].Body);
    }

    [Fact]
    public async Task ApplyStep2IntentAsync_InProgressWithPastEta_FloorsDelayToBuffer()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);
        var eta = _clock.GetUtcNow().AddMinutes(-5);

        await Build().ApplyStep2IntentAsync(
            pending, PhoneNumber.Parse("+2203339999"),
            Step2ReplyIntent.InProgress, eta, CancellationToken.None);

        Assert.Single(_scheduled);
        var (_, delay) = _scheduled[0];
        Assert.Equal(_options.EtaScheduleBuffer, delay);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task ApplyStep2IntentAsync_InProgressAtHorizonExact_SchedulesAtHorizon()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);
        var eta = _clock.GetUtcNow() + _options.MaxEtaHorizon;

        await Build().ApplyStep2IntentAsync(
            pending, PhoneNumber.Parse("+2203339999"),
            Step2ReplyIntent.InProgress, eta, CancellationToken.None);

        Assert.Single(_scheduled);
        var (_, delay) = _scheduled[0];
        Assert.Equal(_options.MaxEtaHorizon + _options.EtaScheduleBuffer, delay);
        Assert.Empty(_added);
        Assert.Single(_claimedWithEta);
    }

    [Fact]
    public async Task JobCompleted_NotInProgressText_RoutesToNo()
    {
        var pending = SeedPendingForStep(FeedbackStep.JobCompleted);

        await Build().HandleAsync(NewInbound("not in progress, gave up"), pending, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.No, _claimed[0].Answer);
        Assert.Empty(_extractStep2Requests);
    }

    [Fact]
    public async Task ApplyStep2IntentAsync_Unclear_StaleWindow_ClaimsSkippedAndAcks()
    {
        var pending = SeedPendingForStep(
            FeedbackStep.JobCompleted,
            promptedAt: _clock.GetUtcNow() - TimeSpan.FromHours(2));

        await Build().ApplyStep2IntentAsync(
            pending, PhoneNumber.Parse("+2203339999"),
            Step2ReplyIntent.Unclear, null, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Single(_sent);
        Assert.Equal(FeedbackCopy.SkippedAck, _sent[0].Body);
    }
}
