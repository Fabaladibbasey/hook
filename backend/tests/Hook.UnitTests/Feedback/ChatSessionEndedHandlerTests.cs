using Hook.Features.ChatSession.SessionAggregate;
using Hook.Features.Feedback;
using Hook.Features.Feedback.Step1Prompt;
using Hook.Features.Matching.MatchAggregate;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wolverine;

namespace Hook.UnitTests.Feedback;

public class ChatSessionEndedHandlerTests
{
    private readonly Mock<IMatchRepository> _matchesMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly List<Step1FeedbackCheck> _published = [];

    public ChatSessionEndedHandlerTests()
    {
        _busMock.Setup(x => x.PublishAsync(It.IsAny<Step1FeedbackCheck>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((msg, _) => _published.Add((Step1FeedbackCheck)msg))
            .Returns(ValueTask.CompletedTask);
    }

    private ChatSessionEndedHandler Build() =>
        new(_matchesMock.Object, NullLogger<ChatSessionEndedHandler>.Instance);

    [Fact]
    public async Task Handle_NoMatchesForChat_PublishesNothing()
    {
        var chatId = Guid.NewGuid();
        _matchesMock.Setup(x => x.GetMatchIdsByChatIdAsync(chatId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await Build().Handle(
            new ChatSessionEndedEvent(chatId, ChatEndReason.User),
            _busMock.Object,
            CancellationToken.None);

        Assert.Empty(_published);
    }

    [Fact]
    public async Task Handle_TwoMatchesForChat_PublishesStep1PerMatch()
    {
        var chatId = Guid.NewGuid();
        var matchA = Guid.NewGuid();
        var matchB = Guid.NewGuid();
        _matchesMock.Setup(x => x.GetMatchIdsByChatIdAsync(chatId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([matchA, matchB]);

        await Build().Handle(
            new ChatSessionEndedEvent(chatId, ChatEndReason.ProductiveSilence),
            _busMock.Object,
            CancellationToken.None);

        Assert.Equal(2, _published.Count);
        Assert.Contains(_published, p => p.MatchId == matchA);
        Assert.Contains(_published, p => p.MatchId == matchB);
    }
}
