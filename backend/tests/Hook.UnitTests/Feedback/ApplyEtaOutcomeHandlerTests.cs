using Hook.Features.Feedback;
using Hook.Features.Feedback.AggregateStats;
using Hook.Features.Feedback.Eta;
using Hook.Features.Feedback.Models;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Core;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.Feedback;

public class ApplyEtaOutcomeHandlerTests
{
    private readonly Mock<IFeedbackRepository> _feedbackMock = new();
    private readonly Mock<IEventBus> _eventBusMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FeedbackOptions _options = new();
    private readonly List<(Guid Id, FeedbackAnswer Answer)> _claimed = [];
    private readonly List<(Guid Id, FeedbackAnswer Answer, DateTimeOffset Eta)> _claimedWithEta = [];
    private readonly List<(object Msg, TimeSpan Delay)> _scheduled = [];
    private readonly List<SendWhatsAppTextRequested> _sent = [];

    private bool _tryClaimResult = true;

    public ApplyEtaOutcomeHandlerTests()
    {
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
        _eventBusMock.Setup(x => x.ScheduleAsync(It.IsAny<It.IsAnyType>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(inv =>
                _scheduled.Add((inv.Arguments[0], (TimeSpan)inv.Arguments[1]))))
            .Returns(Task.CompletedTask);
        _busMock.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextRequested>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((msg, _) => _sent.Add((SendWhatsAppTextRequested)msg))
            .Returns(ValueTask.CompletedTask);
    }

    private ApplyEtaOutcomeHandler Build() =>
        new(_feedbackMock.Object, _eventBusMock.Object, Options.Create(_options), _clock,
            NullLogger<ApplyEtaOutcomeHandler>.Instance);

    private MatchFeedback Pending(FeedbackStep step = FeedbackStep.AwaitingEta, DateTimeOffset? promptedAt = null) =>
        MatchFeedback.CreatePending(Guid.NewGuid(), Guid.NewGuid(), step, promptedAt ?? _clock.GetUtcNow());

    private static PhoneNumber From() => PhoneNumber.Parse("+220300001");

    [Fact]
    public async Task Handle_PendingNotFound_NoOp()
    {
        _feedbackMock.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MatchFeedback?)null);

        await Build().Handle(
            new ApplyEtaOutcome(Guid.NewGuid(), Guid.NewGuid(), _clock.GetUtcNow().AddHours(2), From()),
            _busMock.Object, CancellationToken.None);

        _claimed.ShouldBeEmpty();
        _claimedWithEta.ShouldBeEmpty();
        _scheduled.ShouldBeEmpty();
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_PendingAlreadyClaimed_NoOp()
    {
        var pending = Pending();
        pending.Resolve(FeedbackAnswer.EtaCaptured, _clock.GetUtcNow());
        _feedbackMock.Setup(x => x.GetByIdAsync(pending.Id, It.IsAny<CancellationToken>())).ReturnsAsync(pending);

        await Build().Handle(
            new ApplyEtaOutcome(pending.Id, pending.MatchId, _clock.GetUtcNow().AddHours(2), From()),
            _busMock.Object, CancellationToken.None);

        _claimed.ShouldBeEmpty();
        _claimedWithEta.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_WrongStep_NoOp()
    {
        // Cross-step contamination guard: a retry hitting this handler for a DidYouFind row
        // must not touch any state.
        var pending = Pending(step: FeedbackStep.DidYouFind);
        _feedbackMock.Setup(x => x.GetByIdAsync(pending.Id, It.IsAny<CancellationToken>())).ReturnsAsync(pending);

        await Build().Handle(
            new ApplyEtaOutcome(pending.Id, pending.MatchId, _clock.GetUtcNow().AddHours(2), From()),
            _busMock.Object, CancellationToken.None);

        _claimed.ShouldBeEmpty();
        _claimedWithEta.ShouldBeEmpty();
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_ValidFutureEta_ClaimsEtaCapturedAndSchedules()
    {
        var pending = Pending();
        _feedbackMock.Setup(x => x.GetByIdAsync(pending.Id, It.IsAny<CancellationToken>())).ReturnsAsync(pending);
        var eta = _clock.GetUtcNow().AddHours(3);

        await Build().Handle(
            new ApplyEtaOutcome(pending.Id, pending.MatchId, eta, From()),
            _busMock.Object, CancellationToken.None);

        _claimedWithEta.ShouldHaveSingleItem();
        _claimedWithEta[0].Answer.ShouldBe(FeedbackAnswer.EtaCaptured);
        _claimedWithEta[0].Eta.ShouldBe(eta);
        _scheduled.ShouldHaveSingleItem();
        var (msg, delay) = _scheduled[0];
        ((Step2FeedbackCheck)msg).MatchId.ShouldBe(pending.MatchId);
        delay.ShouldBe(eta - _clock.GetUtcNow() + _options.EtaScheduleBuffer);
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("I'll check back");
    }

    [Fact]
    public async Task Handle_TryClaimWithEtaRaceLost_NoSchedule()
    {
        var pending = Pending();
        _feedbackMock.Setup(x => x.GetByIdAsync(pending.Id, It.IsAny<CancellationToken>())).ReturnsAsync(pending);
        _tryClaimResult = false;

        await Build().Handle(
            new ApplyEtaOutcome(pending.Id, pending.MatchId, _clock.GetUtcNow().AddHours(3), From()),
            _busMock.Object, CancellationToken.None);

        _scheduled.ShouldBeEmpty();
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AiNullWithinRetryWindow_SendsHint()
    {
        var pending = Pending(promptedAt: _clock.GetUtcNow());
        _feedbackMock.Setup(x => x.GetByIdAsync(pending.Id, It.IsAny<CancellationToken>())).ReturnsAsync(pending);

        await Build().Handle(
            new ApplyEtaOutcome(pending.Id, pending.MatchId, EtaUtc: null, From()),
            _busMock.Object, CancellationToken.None);

        _claimed.ShouldBeEmpty();
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("didn't catch");
    }

    [Fact]
    public async Task Handle_AiNullPastRetryWindow_ClaimsSkippedAndSchedulesFallback()
    {
        var pending = Pending(promptedAt: _clock.GetUtcNow() - TimeSpan.FromHours(2));
        _feedbackMock.Setup(x => x.GetByIdAsync(pending.Id, It.IsAny<CancellationToken>())).ReturnsAsync(pending);

        await Build().Handle(
            new ApplyEtaOutcome(pending.Id, pending.MatchId, EtaUtc: null, From()),
            _busMock.Object, CancellationToken.None);

        _claimed.ShouldHaveSingleItem();
        _claimed[0].Answer.ShouldBe(FeedbackAnswer.Skipped);
        _scheduled.ShouldHaveSingleItem();
        _scheduled[0].Delay.ShouldBe(_options.Step2InProgressRecheckDelay);
    }

    [Fact]
    public async Task Handle_EtaBeyondHorizon_FallsBackAsSkipped()
    {
        var pending = Pending();
        _feedbackMock.Setup(x => x.GetByIdAsync(pending.Id, It.IsAny<CancellationToken>())).ReturnsAsync(pending);
        var beyondHorizon = _clock.GetUtcNow().Add(_options.MaxEtaHorizon).AddHours(1);

        await Build().Handle(
            new ApplyEtaOutcome(pending.Id, pending.MatchId, beyondHorizon, From()),
            _busMock.Object, CancellationToken.None);

        _claimedWithEta.ShouldBeEmpty();
        _claimed.ShouldHaveSingleItem();
        _claimed[0].Answer.ShouldBe(FeedbackAnswer.Skipped);
        _scheduled.ShouldHaveSingleItem();
        _scheduled[0].Delay.ShouldBe(_options.Step2InProgressRecheckDelay);
    }

    [Fact]
    public async Task Handle_EtaInPast_ClampsScheduleDelayToBuffer()
    {
        // ETA further in the past than the buffer makes the computed delay negative;
        // the handler clamps to EtaScheduleBuffer so the recheck still fires.
        var pending = Pending();
        _feedbackMock.Setup(x => x.GetByIdAsync(pending.Id, It.IsAny<CancellationToken>())).ReturnsAsync(pending);
        var pastEta = _clock.GetUtcNow() - _options.EtaScheduleBuffer - TimeSpan.FromMinutes(1);

        await Build().Handle(
            new ApplyEtaOutcome(pending.Id, pending.MatchId, pastEta, From()),
            _busMock.Object, CancellationToken.None);

        _claimedWithEta.ShouldHaveSingleItem();
        _scheduled.ShouldHaveSingleItem();
        _scheduled[0].Delay.ShouldBe(_options.EtaScheduleBuffer);
    }
}
