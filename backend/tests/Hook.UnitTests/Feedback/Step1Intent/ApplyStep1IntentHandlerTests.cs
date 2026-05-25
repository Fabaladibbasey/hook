using Hook.Features.Feedback;
using Hook.Features.Feedback.AggregateStats;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.Step1Intent;
using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Shared.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Wolverine;
using Match = Hook.Features.Matching.MatchAggregate.Match;
using ServiceRequestEntity = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest;

namespace Hook.UnitTests.Feedback.Step1Intent;

public class ApplyStep1IntentHandlerTests
{
    private readonly Mock<IFeedbackRepository> _feedbackMock = new();
    private readonly Mock<IServiceRequestRepository> _requestsMock = new();
    private readonly Mock<IMatchRepository> _matchesMock = new();
    private readonly Mock<IEventBus> _eventBusMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FeedbackOptions _options = new();

    private MatchFeedback? _stored;

    public ApplyStep1IntentHandlerTests()
    {
        _feedbackMock.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _stored);
        _feedbackMock.Setup(x => x.TrySaveAsync(It.IsAny<MatchFeedback>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _feedbackMock.Setup(x => x.TryAddPendingAsync(It.IsAny<MatchFeedback>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _matchesMock.Setup(m => m.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Match?)null);
        _matchesMock.Setup(m => m.GetPickedForRequestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Match>());
    }

    private ApplyStep1IntentHandler Build()
    {
        var service = new FeedbackResponseService(
            _feedbackMock.Object, _matchesMock.Object, _requestsMock.Object,
            _eventBusMock.Object, _busMock.Object,
            Options.Create(_options), _clock,
            NullLogger<FeedbackResponseService>.Instance);
        return new ApplyStep1IntentHandler(_feedbackMock.Object, _requestsMock.Object, service);
    }

    private MatchFeedback Pending(
        FeedbackStep step = FeedbackStep.DidYouFind,
        FeedbackAnswer? resolveAs = null)
    {
        var now = _clock.GetUtcNow();
        var p = MatchFeedback.CreatePending(Guid.NewGuid(), Guid.NewGuid(), step, now);
        switch (resolveAs)
        {
            case null:
            case FeedbackAnswer.Pending:
                break;
            case FeedbackAnswer.Yes: p.ClaimYes(now); break;
            case FeedbackAnswer.No: p.ClaimNo(now); break;
            case FeedbackAnswer.Skipped: p.ClaimSkipped(now); break;
            case FeedbackAnswer.WinnerSelected: p.ClaimWinner(now); break;
            case FeedbackAnswer.InProgress: p.ClaimInProgress(now); break;
            case FeedbackAnswer.EtaCaptured: p.ClaimEta(now.AddHours(1), now); break;
            case FeedbackAnswer.NoReasonCaptured: p.CaptureNoReason(null, now); break;
            default: throw new ArgumentOutOfRangeException(nameof(resolveAs), resolveAs, null);
        }
        return p;
    }

    private void StubRequest(Guid requestId, string phone = "+2203339999")
    {
        var request = ServiceRequestEntity.Create(
            phone, "plumbing", new Location(13.4549, -16.5790),
            "Banjul", string.Empty, 5, _clock.GetUtcNow(), sharePhoneNumber: false);
        _requestsMock.Setup(r => r.GetAsync(requestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);
    }

    [Fact]
    public async Task Handle_NullPending_NoOps()
    {
        _stored = null;

        await Build().Handle(
            new ApplyStep1IntentCommand(Guid.NewGuid(), Guid.NewGuid(), Step1ReplyIntent.Yes, null),
            CancellationToken.None);

        // Nothing to assert on a null aggregate — verify no save call happened.
        _feedbackMock.Verify(x => x.TrySaveAsync(It.IsAny<MatchFeedback>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AlreadyClaimedNotPending_NoOps()
    {
        var pending = Pending(resolveAs: FeedbackAnswer.Yes);
        _stored = pending;

        await Build().Handle(
            new ApplyStep1IntentCommand(pending.Id, pending.MatchId, Step1ReplyIntent.Yes, null),
            CancellationToken.None);

        pending.Answer.ShouldBe(FeedbackAnswer.Yes);
        _feedbackMock.Verify(x => x.TrySaveAsync(It.IsAny<MatchFeedback>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WrongStep_NoOps()
    {
        var pending = Pending(step: FeedbackStep.JobCompleted);
        _stored = pending;

        await Build().Handle(
            new ApplyStep1IntentCommand(pending.Id, pending.MatchId, Step1ReplyIntent.Yes, null),
            CancellationToken.None);

        pending.Answer.ShouldBe(FeedbackAnswer.Pending);
    }

    [Fact]
    public async Task Handle_WrongMatchId_NoOps()
    {
        var pending = Pending();
        _stored = pending;

        await Build().Handle(
            new ApplyStep1IntentCommand(pending.Id, Guid.NewGuid(), Step1ReplyIntent.Yes, null),
            CancellationToken.None);

        pending.Answer.ShouldBe(FeedbackAnswer.Pending);
    }

    [Fact]
    public async Task Handle_RequestMissing_NoOps()
    {
        var pending = Pending();
        _stored = pending;
        _requestsMock.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ServiceRequestEntity?)null);

        await Build().Handle(
            new ApplyStep1IntentCommand(pending.Id, pending.MatchId, Step1ReplyIntent.Yes, null, pending.PromptedAt),
            CancellationToken.None);

        pending.Answer.ShouldBe(FeedbackAnswer.Pending);
    }

    [Fact]
    public async Task Handle_HappyPath_InvokesApplyOnService()
    {
        var pending = Pending();
        _stored = pending;
        StubRequest(pending.RequestId);

        await Build().Handle(
            new ApplyStep1IntentCommand(pending.Id, pending.MatchId, Step1ReplyIntent.Yes, null, pending.PromptedAt),
            CancellationToken.None);

        pending.Answer.ShouldBe(FeedbackAnswer.Yes);
    }

    [Fact]
    public async Task Handle_StalePromptedAt_NoOps()
    {
        // Re-prompt between Extract publish and Apply firing: pending.PromptedAt advanced
        // via Reprompt, so the in-flight envelope's snapshot no longer matches the row's
        // prompted-at. Drop to avoid cross-prompt contamination.
        var pending = Pending();
        _stored = pending;
        StubRequest(pending.RequestId);
        var stale = pending.PromptedAt - TimeSpan.FromMinutes(5);

        await Build().Handle(
            new ApplyStep1IntentCommand(pending.Id, pending.MatchId, Step1ReplyIntent.Yes, null, stale),
            CancellationToken.None);

        pending.Answer.ShouldBe(FeedbackAnswer.Pending);
    }
}
