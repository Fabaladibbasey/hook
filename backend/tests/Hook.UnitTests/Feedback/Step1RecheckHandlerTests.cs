using Hook.Features.Feedback;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.Step1Prompt;
using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Wolverine;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;
using ServiceRequestEntity = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest;

namespace Hook.UnitTests.Feedback;

public class Step1RecheckHandlerTests
{
    private readonly Dictionary<Guid, MatchEntity> _matches = [];
    private readonly Dictionary<Guid, ServiceRequestEntity> _requests = [];
    private readonly Dictionary<Guid, IReadOnlyList<MatchEntity>> _pickedByRequest = [];

    private readonly Mock<IFeedbackRepository> _feedbackMock = new();
    private readonly Mock<IMatchRepository> _matchesMock = new();
    private readonly Mock<IServiceRequestRepository> _requestsMock = new();
    private readonly Mock<IMessageBus> _busMock = new();

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FeedbackOptions _options = new();
    private readonly List<(Guid Id, FeedbackAnswer Answer)> _claimed = [];
    private readonly List<Step1PromptDispatchCommand> _published = [];

    private MatchFeedback? _pending;
    private bool _repromptResult = true;

    public Step1RecheckHandlerTests()
    {
        _feedbackMock.Setup(x => x.GetPendingAsync(It.IsAny<Guid>(), FeedbackStep.DidYouFind, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _pending);
        _feedbackMock.Setup(x => x.TryRepromptPendingAsync(
                It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _repromptResult);
        _feedbackMock.Setup(x => x.TryClaimPendingAsync(
                It.IsAny<Guid>(), It.IsAny<FeedbackAnswer>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, FeedbackAnswer, DateTimeOffset, CancellationToken>((id, ans, _, _) => _claimed.Add((id, ans)))
            .ReturnsAsync(true);

        _matchesMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _matches.TryGetValue(id, out var m) ? m : null);
        _matchesMock.Setup(x => x.GetPickedForRequestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid rid, CancellationToken _) =>
                _pickedByRequest.TryGetValue(rid, out var list) ? list : []);

        _requestsMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _requests.TryGetValue(id, out var r) ? r : null);

        _busMock.Setup(x => x.PublishAsync(It.IsAny<Step1PromptDispatchCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((msg, _) => _published.Add((Step1PromptDispatchCommand)msg))
            .Returns(ValueTask.CompletedTask);
    }

    private Step1RecheckHandler Build() =>
        new(_feedbackMock.Object, _matchesMock.Object, _requestsMock.Object,
            Options.Create(_options), _clock,
            NullLogger<Step1RecheckHandler>.Instance);

    private MatchEntity SeedMatch(string clientPhone = "+2203339999")
    {
        var requestId = Guid.NewGuid();
        var match = MatchEntity.Create(requestId, "+2203331234", "plumbing", 0, 0, _clock.GetUtcNow());
        _matches[match.Id] = match;
        _requests[requestId] = ServiceRequestEntity.Create(
            clientPhone, "plumbing", new Location(13.45, -16.6), "Banjul",
            "req-test", 5.0, _clock.GetUtcNow(), false);
        return match;
    }

    [Fact]
    public async Task Handle_PendingNull_NoAction()
    {
        _pending = null;

        await Build().Handle(new Step1RecheckCommand(Guid.NewGuid()), _busMock.Object, CancellationToken.None);

        Assert.Empty(_published);
        Assert.Empty(_claimed);
    }

    [Fact]
    public async Task Handle_RepromptGateFalse_NoDispatch()
    {
        var match = SeedMatch();
        _pending = MatchFeedback.CreatePending(match.Id, match.RequestId, FeedbackStep.DidYouFind, _clock.GetUtcNow());
        _repromptResult = false;

        await Build().Handle(new Step1RecheckCommand(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Empty(_published);
    }

    [Fact]
    public async Task Handle_MatchMissing_NoDispatch()
    {
        var bogusMatchId = Guid.NewGuid();
        _pending = MatchFeedback.CreatePending(bogusMatchId, Guid.NewGuid(), FeedbackStep.DidYouFind, _clock.GetUtcNow());

        await Build().Handle(new Step1RecheckCommand(bogusMatchId), _busMock.Object, CancellationToken.None);

        Assert.Empty(_published);
    }

    [Fact]
    public async Task Handle_RequestMissing_NoDispatch()
    {
        var match = SeedMatch();
        _pending = MatchFeedback.CreatePending(match.Id, match.RequestId, FeedbackStep.DidYouFind, _clock.GetUtcNow());
        _requests.Remove(match.RequestId);

        await Build().Handle(new Step1RecheckCommand(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Empty(_published);
    }

    [Fact]
    public async Task Handle_BadClientPhone_NoDispatch()
    {
        var match = SeedMatch(clientPhone: "not-a-phone");
        _pending = MatchFeedback.CreatePending(match.Id, match.RequestId, FeedbackStep.DidYouFind, _clock.GetUtcNow());

        await Build().Handle(new Step1RecheckCommand(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Empty(_published);
    }

    [Fact]
    public async Task Handle_HappyPath_PublishesPromptDispatch()
    {
        var match = SeedMatch();
        _pending = MatchFeedback.CreatePending(match.Id, match.RequestId, FeedbackStep.DidYouFind, _clock.GetUtcNow());

        await Build().Handle(new Step1RecheckCommand(match.Id), _busMock.Object, CancellationToken.None);

        var dispatch = Assert.Single(_published);
        Assert.Equal(_pending!.Id, dispatch.FeedbackId);
        Assert.Equal(match.Id, dispatch.MatchId);
        Assert.Equal(match.RequestId, dispatch.RequestId);
        Assert.Equal("plumbing", dispatch.ServiceSlug);
        Assert.Equal(string.Empty, dispatch.PickedFormatted);
    }

    [Fact]
    public async Task Handle_CapExceeded_ClaimsSkippedAndExits()
    {
        // Default Step1RecheckCount=0; with Step1MaxRechecks=-1 any RecheckCount
        // already exceeds the cap. Drives the cap-exceeded short-circuit without
        // having to mutate the private setter through reflection.
        var match = SeedMatch();
        _pending = MatchFeedback.CreatePending(match.Id, match.RequestId, FeedbackStep.DidYouFind, _clock.GetUtcNow());

        var capped = new Step1RecheckHandler(
            _feedbackMock.Object, _matchesMock.Object, _requestsMock.Object,
            Options.Create(new FeedbackOptions { Step1MaxRechecks = -1 }), _clock,
            NullLogger<Step1RecheckHandler>.Instance);

        await capped.Handle(new Step1RecheckCommand(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Empty(_published);
    }
}
