using Hook.Features.Feedback;
using Hook.Features.Feedback.AggregateStats;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.Step2Intent;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.Geocoding.Models;
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

namespace Hook.UnitTests.Feedback.Step2Intent;

public class ApplyStep2IntentHandlerTests
{
    private readonly Mock<IFeedbackRepository> _feedbackMock = new();
    private readonly Mock<IServiceRequestRepository> _requestsMock = new();
    private readonly Mock<IMatchRepository> _matchesMock = new();
    private readonly Mock<IEventBus> _eventBusMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FeedbackOptions _options = new();

    private readonly List<(Guid Id, FeedbackAnswer Answer)> _claimed = [];

    public ApplyStep2IntentHandlerTests()
    {
        _feedbackMock.Setup(x => x.TryClaimPendingAsync(
                It.IsAny<Guid>(), It.IsAny<FeedbackAnswer>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, FeedbackAnswer, DateTimeOffset, CancellationToken>(
                (id, answer, _, _) => _claimed.Add((id, answer)))
            .ReturnsAsync(true);
    }

    private ApplyStep2IntentHandler Build()
    {
        var service = new FeedbackResponseService(
            _feedbackMock.Object, _matchesMock.Object,
            _eventBusMock.Object, _busMock.Object,
            Options.Create(_options), _clock,
            NullLogger<FeedbackResponseService>.Instance);
        return new ApplyStep2IntentHandler(_feedbackMock.Object, _requestsMock.Object, service);
    }

    private MatchFeedback Pending(
        FeedbackStep step = FeedbackStep.JobCompleted,
        FeedbackAnswer? resolveAs = null,
        Guid? matchId = null,
        Guid? requestId = null)
    {
        var row = MatchFeedback.CreatePending(
            matchId ?? Guid.NewGuid(),
            requestId ?? Guid.NewGuid(),
            step,
            _clock.GetUtcNow());
        if (resolveAs is { } answer) row.Resolve(answer, _clock.GetUtcNow());
        return row;
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
        _feedbackMock.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MatchFeedback?)null);

        await Build().Handle(
            new ApplyStep2IntentCommand(Guid.NewGuid(), Guid.NewGuid(), Step2ReplyIntent.Yes, null),
            CancellationToken.None);

        _claimed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AlreadyClaimedNotPending_NoOps()
    {
        var pending = Pending(resolveAs: FeedbackAnswer.Yes);
        _feedbackMock.Setup(x => x.GetByIdAsync(pending.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);

        await Build().Handle(
            new ApplyStep2IntentCommand(pending.Id, pending.MatchId, Step2ReplyIntent.Yes, null),
            CancellationToken.None);

        _claimed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_WrongStep_NoOps()
    {
        var pending = Pending(step: FeedbackStep.AwaitingEta);
        _feedbackMock.Setup(x => x.GetByIdAsync(pending.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);

        await Build().Handle(
            new ApplyStep2IntentCommand(pending.Id, pending.MatchId, Step2ReplyIntent.Yes, null),
            CancellationToken.None);

        _claimed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_WrongMatchId_NoOps()
    {
        var pending = Pending();
        _feedbackMock.Setup(x => x.GetByIdAsync(pending.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);

        await Build().Handle(
            new ApplyStep2IntentCommand(pending.Id, Guid.NewGuid(), Step2ReplyIntent.Yes, null),
            CancellationToken.None);

        _claimed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_RequestMissing_NoOps()
    {
        var pending = Pending();
        _feedbackMock.Setup(x => x.GetByIdAsync(pending.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);
        _requestsMock.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ServiceRequestEntity?)null);

        await Build().Handle(
            new ApplyStep2IntentCommand(pending.Id, pending.MatchId, Step2ReplyIntent.Yes, null),
            CancellationToken.None);

        _claimed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_HappyPath_InvokesApplyOnService()
    {
        var pending = Pending();
        _feedbackMock.Setup(x => x.GetByIdAsync(pending.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);
        StubRequest(pending.RequestId);
        _matchesMock.Setup(m => m.GetAsync(pending.MatchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Match?)null);

        await Build().Handle(
            new ApplyStep2IntentCommand(pending.Id, pending.MatchId, Step2ReplyIntent.Yes, null),
            CancellationToken.None);

        _claimed.ShouldHaveSingleItem();
        _claimed[0].Answer.ShouldBe(FeedbackAnswer.Yes);
    }
}
