using Hook.Features.Feedback;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.Step1Prompt;
using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp.Phone;
using Moq;
using Wolverine;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;
using ServiceRequestEntity = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest;

namespace Hook.UnitTests.Feedback;

public class Step1FeedbackHandlerTests
{
    private readonly Dictionary<Guid, MatchEntity> _matches = new();
    private readonly Dictionary<Guid, IReadOnlyList<MatchEntity>> _requestMatches = new();
    private readonly Dictionary<Guid, ServiceRequestEntity> _requests = new();
    private readonly List<MatchFeedback> _added = new();
    private readonly List<Step1PromptDispatchRequested> _published = new();

    private readonly Mock<IFeedbackRepository> _feedbackMock = new();
    private readonly Mock<IMatchRepository> _matchesMock = new();
    private readonly Mock<IServiceRequestRepository> _requestsMock = new();
    private readonly Mock<IMessageBus> _busMock = new();

    private bool _tryAddResult = true;

    public Step1FeedbackHandlerTests()
    {
        _feedbackMock.Setup(x => x.TryAddPendingAsync(It.IsAny<MatchFeedback>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MatchFeedback f, CancellationToken _) =>
            {
                if (_tryAddResult) _added.Add(f);
                return _tryAddResult;
            });

        _matchesMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _matches.TryGetValue(id, out var m) ? m : null);
        _matchesMock.Setup(x => x.GetForRequestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid requestId, CancellationToken _) =>
            {
                if (!_requestMatches.TryGetValue(requestId, out var list))
                    throw new InvalidOperationException($"missing seed for {requestId}");
                return list;
            });

        _requestsMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _requests.TryGetValue(id, out var r) ? r : null);

        _busMock.Setup(x => x.PublishAsync(It.IsAny<Step1PromptDispatchRequested>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((msg, _) => _published.Add((Step1PromptDispatchRequested)msg))
            .Returns(ValueTask.CompletedTask);
    }

    private Step1FeedbackHandler Build() =>
        new(_matchesMock.Object, _requestsMock.Object, _feedbackMock.Object);

    private MatchEntity SeedMatch()
    {
        var requestId = Guid.NewGuid();
        var match = new MatchEntity
        {
            RequestId = requestId,
            ProviderPhone = "+2203331234",
            ServiceSlug = "plumbing"
        };
        _matches[match.Id] = match;
        _requestMatches[requestId] = new[] { match };

        _requests[requestId] = ServiceRequestEntity.Create(
            "+2203339999", "plumbing",
            new Location(13.45, -16.6), "Banjul",
            "req-test", 5.0, DateTimeOffset.UtcNow, false);
        return match;
    }

    [Fact]
    public async Task Handle_HappyPath_ReservesPendingAndPublishesDispatch()
    {
        var match = SeedMatch();

        await Build().Handle(new Step1FeedbackCheck(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Single(_added);
        Assert.Single(_published);
        Assert.Equal(match.Id, _published[0].MatchId);
        Assert.Equal(match.RequestId, _published[0].RequestId);
        Assert.Null(_published[0].PickedFormatted);
    }

    [Fact]
    public async Task Handle_LosesPartialUniqueRace_ExitsSilently()
    {
        // Both partial unique indexes (per-match and per-request) collapse to the same
        // observable here: TryAddPendingAsync returns false, handler exits before publish.
        var match = SeedMatch();
        _tryAddResult = false;

        await Build().Handle(new Step1FeedbackCheck(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Empty(_added);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Handle_PopulatesRequestIdOnPendingRow()
    {
        var match = SeedMatch();

        await Build().Handle(new Step1FeedbackCheck(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Single(_added);
        Assert.Equal(match.RequestId, _added[0].RequestId);
    }

    [Fact]
    public async Task Handle_MatchMissing_Silent()
    {
        await Build().Handle(new Step1FeedbackCheck(Guid.NewGuid()), _busMock.Object, CancellationToken.None);

        Assert.Empty(_added);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Handle_RequestMissing_Silent()
    {
        var match = SeedMatch();
        _requests.Remove(match.RequestId);

        await Build().Handle(new Step1FeedbackCheck(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Empty(_added);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Handle_BadClientPhone_Silent()
    {
        var match = SeedMatch();
        _requests[match.RequestId] = ServiceRequestEntity.Create(
            "not-a-phone", "plumbing",
            new Location(13.45, -16.6), "Banjul",
            "req-test", 5.0, DateTimeOffset.UtcNow, false);

        await Build().Handle(new Step1FeedbackCheck(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Empty(_added);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Handle_MultiPick_PickedFormattedHonorsRepositoryOrder()
    {
        var requestId = Guid.NewGuid();
        var match = new MatchEntity
        {
            RequestId = requestId,
            ProviderPhone = "+2203331234",
            ServiceSlug = "plumbing",
            Score = 0.5,
            PickedAt = DateTimeOffset.UtcNow
        };
        _matches[match.Id] = match;
        _requests[requestId] = ServiceRequestEntity.Create(
            "+2203339999", "plumbing",
            new Location(13.45, -16.6), "Banjul",
            "req-test", 5.0, DateTimeOffset.UtcNow, false);

        var sibling = new MatchEntity
        {
            RequestId = match.RequestId,
            ProviderPhone = "+2204445678",
            ServiceSlug = "plumbing",
            CreatedAt = match.CreatedAt.AddSeconds(-1),
            PickedAt = DateTimeOffset.UtcNow,
            Score = 0.9
        };
        _matches[sibling.Id] = sibling;
        _requestMatches[match.RequestId] = new[] { sibling, match };

        await Build().Handle(new Step1FeedbackCheck(match.Id), _busMock.Object, CancellationToken.None);

        Assert.Single(_published);
        var rendered = _published[0].PickedFormatted;
        Assert.NotNull(rendered);
        var firstSlot = rendered!.IndexOf("1)", StringComparison.Ordinal);
        var secondSlot = rendered.IndexOf("2)", StringComparison.Ordinal);
        Assert.True(firstSlot >= 0 && secondSlot > firstSlot);
        var slot1 = rendered.Substring(firstSlot, secondSlot - firstSlot);
        var slot2 = rendered[secondSlot..];
        Assert.Contains("78", slot1, StringComparison.Ordinal);
        Assert.Contains("34", slot2, StringComparison.Ordinal);
    }
}
