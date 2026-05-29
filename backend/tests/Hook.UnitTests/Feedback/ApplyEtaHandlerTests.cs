using Hook.Features.Feedback;
using Hook.Features.Feedback.AggregateStats;
using Hook.Features.Feedback.Eta;
using Hook.Features.Feedback.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
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

public class ApplyEtaHandlerTests
{
    private readonly Mock<IFeedbackRepository> _feedbackMock = new();
    private readonly Mock<IEventBus> _eventBusMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FeedbackOptions _options = new();
    private readonly List<(object Msg, TimeSpan Delay)> _scheduled = [];
    private readonly List<SendWhatsAppTextCommand> _sent = [];

    private MatchFeedback? _stored;
    private bool _saveResult = true;

    public ApplyEtaHandlerTests()
    {
        _feedbackMock.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _stored);
        _feedbackMock.Setup(x => x.TrySaveAsync(It.IsAny<MatchFeedback>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _saveResult);
        _eventBusMock.Setup(x => x.ScheduleAsync(It.IsAny<It.IsAnyType>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(inv =>
                _scheduled.Add((inv.Arguments[0], (TimeSpan)inv.Arguments[1]))))
            .Returns(Task.CompletedTask);
        _busMock.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((msg, _) => _sent.Add((SendWhatsAppTextCommand)msg))
            .Returns(ValueTask.CompletedTask);
    }

    private ApplyEtaHandler Build()
    {
        var service = new FeedbackResponseService(
            _feedbackMock.Object,
            new Mock<IMatchRepository>().Object,
            new Mock<IServiceRequestRepository>().Object,
            _eventBusMock.Object,
            _busMock.Object,
            Options.Create(_options),
            _clock,
            NullLogger<FeedbackResponseService>.Instance);
        return new ApplyEtaHandler(
            _feedbackMock.Object, _eventBusMock.Object, service, Options.Create(_options),
            _clock, NullLogger<ApplyEtaHandler>.Instance);
    }

    private MatchFeedback Pending(FeedbackStep step = FeedbackStep.AwaitingEta, DateTimeOffset? promptedAt = null) =>
        MatchFeedback.CreatePending(Guid.NewGuid(), Guid.NewGuid(), step, promptedAt ?? _clock.GetUtcNow());

    private static PhoneNumber From() => PhoneNumber.Parse("+220300001");

    [Fact]
    public async Task Handle_PendingNotFound_NoOp()
    {
        _stored = null;

        await Build().Handle(
            new ApplyEtaCommand(Guid.NewGuid(), Guid.NewGuid(), _clock.GetUtcNow().AddHours(2), From()),
            _busMock.Object, CancellationToken.None);

        _scheduled.ShouldBeEmpty();
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_PendingAlreadyClaimed_NoOp()
    {
        var pending = Pending();
        // Drive aggregate into a claimed state via the legitimate transition so the
        // pre-check in the handler sees Answer != Pending.
        pending.ClaimEta(_clock.GetUtcNow().AddHours(1), _clock.GetUtcNow());
        _stored = pending;

        await Build().Handle(
            new ApplyEtaCommand(pending.Id, pending.MatchId, _clock.GetUtcNow().AddHours(2), From()),
            _busMock.Object, CancellationToken.None);

        _scheduled.ShouldBeEmpty();
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_WrongStep_NoOp()
    {
        // Cross-step contamination guard: a retry hitting this handler for a DidYouFind row
        // must not touch any state.
        var pending = Pending(step: FeedbackStep.DidYouFind);
        _stored = pending;

        await Build().Handle(
            new ApplyEtaCommand(pending.Id, pending.MatchId, _clock.GetUtcNow().AddHours(2), From()),
            _busMock.Object, CancellationToken.None);

        pending.Answer.ShouldBe(FeedbackAnswer.Pending);
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_ValidFutureEta_ClaimsEtaCapturedAndSchedules()
    {
        var pending = Pending();
        _stored = pending;
        var eta = _clock.GetUtcNow().AddHours(3);

        await Build().Handle(
            new ApplyEtaCommand(pending.Id, pending.MatchId, eta, From()),
            _busMock.Object, CancellationToken.None);

        pending.Answer.ShouldBe(FeedbackAnswer.EtaCaptured);
        pending.EtaUtc.ShouldBe(eta);
        _scheduled.ShouldHaveSingleItem();
        var (msg, delay) = _scheduled[0];
        ((Step2FeedbackCheck)msg).MatchId.ShouldBe(pending.MatchId);
        delay.ShouldBe(eta - _clock.GetUtcNow() + _options.EtaScheduleBuffer);
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("I'll check back");
    }

    [Fact]
    public async Task Handle_TrySaveRaceLost_NoSchedule()
    {
        var pending = Pending();
        _stored = pending;
        _saveResult = false;

        await Build().Handle(
            new ApplyEtaCommand(pending.Id, pending.MatchId, _clock.GetUtcNow().AddHours(3), From()),
            _busMock.Object, CancellationToken.None);

        _scheduled.ShouldBeEmpty();
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AiNullWithinRetryWindow_SendsHint()
    {
        var pending = Pending(promptedAt: _clock.GetUtcNow());
        _stored = pending;

        await Build().Handle(
            new ApplyEtaCommand(pending.Id, pending.MatchId, EtaUtc: null, From()),
            _busMock.Object, CancellationToken.None);

        pending.Answer.ShouldBe(FeedbackAnswer.Pending);
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("didn't catch");
    }

    [Fact]
    public async Task Handle_AiNullPastRetryWindow_ClaimsSkippedAndSchedulesFallback()
    {
        var pending = Pending(promptedAt: _clock.GetUtcNow() - TimeSpan.FromHours(2));
        _stored = pending;

        await Build().Handle(
            new ApplyEtaCommand(pending.Id, pending.MatchId, EtaUtc: null, From()),
            _busMock.Object, CancellationToken.None);

        pending.Answer.ShouldBe(FeedbackAnswer.Skipped);
        _scheduled.ShouldHaveSingleItem();
        _scheduled[0].Delay.ShouldBe(_options.Step2InProgressRecheckDelay);
        _sent.ShouldHaveSingleItem().Text.ShouldBe(FeedbackCopy.SkippedAck);
    }

    [Fact]
    public async Task Handle_EtaBeyondHorizon_FallsBackAsSkipped()
    {
        var pending = Pending();
        _stored = pending;
        var beyondHorizon = _clock.GetUtcNow().Add(_options.MaxEtaHorizon).AddHours(1);

        await Build().Handle(
            new ApplyEtaCommand(pending.Id, pending.MatchId, beyondHorizon, From()),
            _busMock.Object, CancellationToken.None);

        pending.Answer.ShouldBe(FeedbackAnswer.Skipped);
        pending.EtaUtc.ShouldBeNull();
        _scheduled.ShouldHaveSingleItem();
        _scheduled[0].Delay.ShouldBe(_options.Step2InProgressRecheckDelay);
        _sent.ShouldHaveSingleItem().Text.ShouldBe(FeedbackCopy.SkippedAck);
    }

    [Fact]
    public async Task Handle_EtaInPast_ClampsScheduleDelayToBuffer()
    {
        // ETA further in the past than the buffer makes the computed delay negative;
        // the handler clamps to EtaScheduleBuffer so the recheck still fires.
        var pending = Pending();
        _stored = pending;
        var pastEta = _clock.GetUtcNow() - _options.EtaScheduleBuffer - TimeSpan.FromMinutes(1);

        await Build().Handle(
            new ApplyEtaCommand(pending.Id, pending.MatchId, pastEta, From()),
            _busMock.Object, CancellationToken.None);

        pending.Answer.ShouldBe(FeedbackAnswer.EtaCaptured);
        pending.EtaUtc.ShouldBe(pastEta);
        _scheduled.ShouldHaveSingleItem();
        _scheduled[0].Delay.ShouldBe(_options.EtaScheduleBuffer);
        _sent.ShouldHaveSingleItem().Text.ShouldContain("I'll check back");
    }
}
