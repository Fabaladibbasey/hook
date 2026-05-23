using Hook.Features.Ai;
using Hook.Features.Feedback.Eta;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.Feedback;

public class ExtractEtaHandlerTests
{
    private readonly Mock<IConversationAi> _aiMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly List<ApplyEtaCommand> _invoked = [];

    public ExtractEtaHandlerTests()
    {
        _busMock.Setup(x => x.PublishAsync(It.IsAny<ApplyEtaCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions?>((m, _) => _invoked.Add((ApplyEtaCommand)m))
            .Returns(ValueTask.CompletedTask);
    }

    private ExtractEtaHandler Build() => new(_aiMock.Object, _clock);

    private static PhoneNumber From() => PhoneNumber.Parse("+220300001");

    [Fact]
    public async Task Handle_AiReturnsEta_InvokesApplyOutcomeWithValue()
    {
        var eta = _clock.GetUtcNow().AddHours(3);
        _aiMock.Setup(x => x.ExtractEtaAsync("in 3 hours", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(eta);

        var pendingId = Guid.NewGuid();
        var matchId = Guid.NewGuid();
        await Build().Handle(
            new ExtractEtaCommand(pendingId, matchId, From(), "in 3 hours"),
            _busMock.Object, CancellationToken.None);

        _invoked.ShouldHaveSingleItem();
        _invoked[0].PendingId.ShouldBe(pendingId);
        _invoked[0].MatchId.ShouldBe(matchId);
        _invoked[0].EtaUtc.ShouldBe(eta);
    }

    [Fact]
    public async Task Handle_AdapterReturnsNull_InvokesApplyOutcomeWithNullEta()
    {
        // OllamaConversationAi.TryCallAsync absorbs transport failures and returns
        // null; the handler passes it through so ApplyEtaHandler can fall
        // back to the fixed Step2InProgressRecheckDelay.
        _aiMock.Setup(x => x.ExtractEtaAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset?)null);

        await Build().Handle(
            new ExtractEtaCommand(Guid.NewGuid(), Guid.NewGuid(), From(), "in 3 hours"),
            _busMock.Object, CancellationToken.None);

        _invoked.ShouldHaveSingleItem();
        _invoked[0].EtaUtc.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_OuterCancellation_RethrowsAndDoesNotInvoke()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _aiMock.Setup(x => x.ExtractEtaAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => Build().Handle(
            new ExtractEtaCommand(Guid.NewGuid(), Guid.NewGuid(), From(), "in 3 hours"),
            _busMock.Object, cts.Token));

        _invoked.ShouldBeEmpty();
    }
}
