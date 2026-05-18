using Hook.Features.Feedback;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.Step2Prompt;
using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Moq;
using Wolverine;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;
using ServiceRequestEntity = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest;

namespace Hook.UnitTests.Feedback;

public class Step2FeedbackHandlerTests
{
    private readonly Dictionary<Guid, MatchEntity> _matches = [];
    private readonly Dictionary<Guid, ServiceRequestEntity> _requests = [];
    private readonly List<MatchFeedback> _added = [];
    private readonly List<Step2PromptDispatchRequested> _published = [];

    private readonly Mock<IFeedbackRepository> _feedbackMock = new();
    private readonly Mock<IMatchRepository> _matchesMock = new();
    private readonly Mock<IServiceRequestRepository> _requestsMock = new();
    private readonly Mock<IMessageBus> _busMock = new();

    private bool _tryAddResult = true;
    private MatchFeedback? _latestJobCompleted;

    public Step2FeedbackHandlerTests()
    {
        _feedbackMock.Setup(x => x.TryAddPendingAsync(It.IsAny<MatchFeedback>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MatchFeedback f, CancellationToken _) =>
            {
                if (_tryAddResult) _added.Add(f);
                return _tryAddResult;
            });
        _feedbackMock.Setup(x => x.GetLatestByMatchAndStepAsync(It.IsAny<Guid>(), It.IsAny<FeedbackStep>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, FeedbackStep step, CancellationToken _) =>
                step == FeedbackStep.JobCompleted ? _latestJobCompleted : null);

        _matchesMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _matches.TryGetValue(id, out var m) ? m : null);

        _requestsMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _requests.TryGetValue(id, out var r) ? r : null);

        _busMock.Setup(x => x.PublishAsync(It.IsAny<Step2PromptDispatchRequested>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((msg, _) => _published.Add((Step2PromptDispatchRequested)msg))
            .Returns(ValueTask.CompletedTask);
    }

    private Step2FeedbackHandler Build() =>
        new(_feedbackMock.Object, _matchesMock.Object, _requestsMock.Object);

    private MatchEntity SeedMatch()
    {
        var match = new MatchEntity
        {
            RequestId = Guid.NewGuid(),
            ProviderPhone = "+2203331234",
            ServiceSlug = "plumbing"
        };
        _matches[match.Id] = match;
        _requests[match.RequestId] = ServiceRequestEntity.Create(
            "+2203339999", "plumbing",
            new Location(13.45, -16.6), "Banjul",
            "req-test", 5.0, DateTimeOffset.UtcNow, false);
        return match;
    }

    [Fact]
    public async Task Handle_MatchMissing_NoOp()
    {
        await Build().Handle(new Step2FeedbackCheck(Guid.NewGuid()), _busMock.Object, CancellationToken.None);

        Assert.Empty(_published);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Handle_RequestMissing_NoOp()
    {
        var match = SeedMatch();
        _requests.Remove(match.RequestId);

        await Build().Handle(new Step2FeedbackCheck(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Empty(_published);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Handle_BadClientPhone_NoOp()
    {
        var match = SeedMatch();
        _requests[match.RequestId] = ServiceRequestEntity.Create(
            "not-a-phone", "plumbing",
            new Location(13.45, -16.6), "Banjul",
            "req-test", 5.0, DateTimeOffset.UtcNow, false);

        await Build().Handle(new Step2FeedbackCheck(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Empty(_published);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Handle_HappyPath_ReservesAndPublishesDispatch()
    {
        var match = SeedMatch();

        await Build().Handle(new Step2FeedbackCheck(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Single(_added);
        Assert.Single(_published);
        Assert.Equal(match.Id, _published[0].MatchId);
    }

    [Fact]
    public async Task Handle_TryAddPendingFails_DoesNotPublish()
    {
        var match = SeedMatch();
        _tryAddResult = false;

        await Build().Handle(new Step2FeedbackCheck(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Empty(_published);
    }

    [Fact]
    public async Task Handle_AlreadyCompletedYes_DoesNotRePrompt()
    {
        var match = SeedMatch();
        _latestJobCompleted = new MatchFeedback
        {
            MatchId = match.Id,
            RequestId = match.RequestId,
            Step = FeedbackStep.JobCompleted,
            Answer = FeedbackAnswer.Yes
        };

        await Build().Handle(new Step2FeedbackCheck(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Empty(_published);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Handle_AlreadyCompletedNo_DoesNotRePrompt()
    {
        var match = SeedMatch();
        _latestJobCompleted = new MatchFeedback
        {
            MatchId = match.Id,
            RequestId = match.RequestId,
            Step = FeedbackStep.JobCompleted,
            Answer = FeedbackAnswer.No
        };

        await Build().Handle(new Step2FeedbackCheck(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Empty(_published);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Handle_PreviousJobCompletedInProgress_StillPublishes()
    {
        var match = SeedMatch();
        _latestJobCompleted = new MatchFeedback
        {
            MatchId = match.Id,
            RequestId = match.RequestId,
            Step = FeedbackStep.JobCompleted,
            Answer = FeedbackAnswer.InProgress
        };

        await Build().Handle(new Step2FeedbackCheck(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Single(_published);
        Assert.Single(_added);
    }
}
