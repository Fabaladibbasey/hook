using Hook.Features.Ai;
using Hook.Features.ServiceRequest.Create.ConfirmIntent;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.ServiceRequest.Create.ConfirmIntent;

public class ExtractConfirmIntentHandlerTests
{
    private readonly Mock<IConversationAi> _aiMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly List<ApplyConfirmIntentCommand> _invoked = [];

    public ExtractConfirmIntentHandlerTests()
    {
        _busMock.Setup(x => x.PublishAsync(It.IsAny<ApplyConfirmIntentCommand>(), It.IsAny<DeliveryOptions?>()))
            .Callback<object, DeliveryOptions?>((m, _) => _invoked.Add((ApplyConfirmIntentCommand)m))
            .Returns(ValueTask.CompletedTask);
    }

    private ExtractConfirmIntentHandler Build() => new(_aiMock.Object);

    [Fact]
    public async Task Handle_PublishesApplyCommand_WithAiResultAndStamp()
    {
        var stamp = DateTimeOffset.UtcNow;
        _aiMock.Setup(x => x.ExtractConfirmIntentAsync(
                "plumbing", "yeah that's exactly what I need", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfirmReplyIntent.Yes);

        await Build().Handle(
            new ExtractConfirmIntentCommand("+220700009001",
                "plumbing", "yeah that's exactly what I need", stamp),
            _busMock.Object, CancellationToken.None);

        var cmd = _invoked.ShouldHaveSingleItem();
        cmd.Phone.ShouldBe("+220700009001");
        cmd.Intent.ShouldBe(ConfirmReplyIntent.Yes);
        cmd.DraftStampedAt.ShouldBe(stamp);
        _aiMock.Verify(x => x.ExtractConfirmIntentAsync(
            "plumbing", "yeah that's exactly what I need", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_AiFallbackUnsure_StillPublishesApply()
    {
        // OllamaConversationAi.TryCallAsync absorbs failure and returns Unsure;
        // the handler must still publish so ApplyConfirmIntentHandler re-prompts.
        var stamp = DateTimeOffset.UtcNow;
        _aiMock.Setup(x => x.ExtractConfirmIntentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfirmReplyIntent.Unsure);

        await Build().Handle(
            new ExtractConfirmIntentCommand("+220700009002", "plumbing", "what?", stamp),
            _busMock.Object, CancellationToken.None);

        _invoked.ShouldHaveSingleItem();
        _invoked[0].Phone.ShouldBe("+220700009002");
        _invoked[0].Intent.ShouldBe(ConfirmReplyIntent.Unsure);
        _invoked[0].DraftStampedAt.ShouldBe(stamp);
        _aiMock.Verify(x => x.ExtractConfirmIntentAsync(
            "plumbing", "what?", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // OCE absorption lives in OllamaConversationAi.TryCallAsync, not in this
    // handler — the handler-level OCE rethrow test was a Moq tautology and has
    // been removed. Production absorbed-fallback is exercised via the
    // FakeConversationAi+real handler path in ConfirmIntentPipelineTests.
}
