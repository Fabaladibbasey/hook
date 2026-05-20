using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;
using Hook.Features.Whatsapp.ReceiveWebhook;
using Hook.Features.Whatsapp.ReceiveWebhook.ClassifyInboundIntent;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.Whatsapp;

public class ClassifyInboundIntentHandlerTests
{
    private readonly Mock<IConversationAi> _aiMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly List<RouteClassifiedIntent> _routed = [];

    public ClassifyInboundIntentHandlerTests()
    {
        _busMock.Setup(x => x.InvokeAsync(It.IsAny<RouteClassifiedIntent>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .Callback<object, CancellationToken, TimeSpan?>((m, _, _) => _routed.Add((RouteClassifiedIntent)m))
            .Returns(Task.CompletedTask);
    }

    private ClassifyInboundIntentHandler Build() =>
        new(_aiMock.Object, NullLogger<ClassifyInboundIntentHandler>.Instance);

    private static InboundMessage Inbound(string text) =>
        new("m-" + Guid.NewGuid(), PhoneNumber.Parse("+220300001"),
            DateTimeOffset.UtcNow, InboundMessageKind.Text, text, Location: null, RawJson: null);

    [Fact]
    public async Task Handle_AiReturnsIntent_InvokesRouteClassifiedIntent()
    {
        var msg = Inbound("I need a plumber");
        var detected = new IntentDetectionResult(IntentKind.ServiceRequest, 0.92, "en", "ai");
        _aiMock.Setup(x => x.DetectIntentAsync("I need a plumber", It.IsAny<CancellationToken>()))
            .ReturnsAsync(detected);

        await Build().Handle(new ClassifyInboundIntentRequested(msg), _busMock.Object, CancellationToken.None);

        _routed.ShouldHaveSingleItem();
        _routed[0].Message.MessageId.ShouldBe(msg.MessageId);
        _routed[0].Detected.Intent.ShouldBe(IntentKind.ServiceRequest);
        _routed[0].Detected.Confidence.ShouldBe(0.92);
    }

    [Fact]
    public async Task Handle_AiThrows_FallsBackToUnknownIntent()
    {
        _aiMock.Setup(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("ollama down"));

        await Build().Handle(new ClassifyInboundIntentRequested(Inbound("hi")), _busMock.Object, CancellationToken.None);

        _routed.ShouldHaveSingleItem();
        _routed[0].Detected.Intent.ShouldBe(IntentKind.Unknown);
        _routed[0].Detected.Notes.ShouldBe("exception");
    }

    [Fact]
    public async Task Handle_CancellationRequested_RethrowsAndDoesNotRoute()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _aiMock.Setup(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => Build().Handle(
            new ClassifyInboundIntentRequested(Inbound("hi")), _busMock.Object, cts.Token));

        _routed.ShouldBeEmpty();
    }
}
