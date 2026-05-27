using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.MetaTemplates;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;
using Hook.Features.Whatsapp.ReceiveWebhook.ClassifyInboundIntent;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.Whatsapp;

public class ClassifyInboundIntentHandlerTests
{
    private readonly Mock<IConversationAi> _aiMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly Mock<IWhatsappContactRepository> _contactsMock = new();
    private readonly List<RouteClassifiedIntentCommand> _routed = [];

    public ClassifyInboundIntentHandlerTests()
    {
        _busMock.Setup(x => x.PublishAsync(It.IsAny<RouteClassifiedIntentCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions?>((m, _) => _routed.Add((RouteClassifiedIntentCommand)m))
            .Returns(ValueTask.CompletedTask);
    }

    private ClassifyInboundIntentHandler Build() => new(_aiMock.Object, _contactsMock.Object);

    private static InboundMessage Inbound(string text) =>
        new("m-" + Guid.NewGuid(), PhoneNumber.Parse("+220300001"),
            DateTimeOffset.UtcNow, InboundMessageKind.Text, text, Location: null, RawJson: null);

    [Fact]
    public async Task Handle_AiReturnsIntent_InvokesRouteClassifiedIntentCommand()
    {
        var msg = Inbound("I need a plumber");
        var detected = new IntentDetectionResult(IntentKind.ServiceRequest, 0.92, "en", "ai");
        _aiMock.Setup(x => x.DetectIntentAsync("I need a plumber", It.IsAny<CancellationToken>()))
            .ReturnsAsync(detected);

        await Build().Handle(new ClassifyInboundIntentCommand(msg), _busMock.Object, CancellationToken.None);

        _routed.ShouldHaveSingleItem();
        _routed[0].Message.MessageId.ShouldBe(msg.MessageId);
        _routed[0].Detected.Intent.ShouldBe(IntentKind.ServiceRequest);
        _routed[0].Detected.Confidence.ShouldBe(0.92);
    }

    [Fact]
    public async Task Handle_AdapterReturnsNeutralFallback_RoutesUnknown()
    {
        // OllamaConversationAi.TryCallAsync absorbs transport failures and returns
        // this neutral value; the handler must pass it through without inspection.
        var fallback = new IntentDetectionResult(IntentKind.Unknown, 0, "en", "exception");
        _aiMock.Setup(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fallback);

        await Build().Handle(new ClassifyInboundIntentCommand(Inbound("hi")), _busMock.Object, CancellationToken.None);

        _routed.ShouldHaveSingleItem();
        _routed[0].Detected.Intent.ShouldBe(IntentKind.Unknown);
        _routed[0].Detected.Notes.ShouldBe("exception");
    }

    [Fact]
    public async Task Handle_AiReturnsLocale_PersistsSanitizedLocaleViaContactRepo()
    {
        // LLM classifier locale fan-out: every classified inbound persists the
        // sanitized BCP-47 code so the next deterministic Q&A short-circuit can
        // read it instead of pinning "en".
        var msg = Inbound("comment ça marche");
        var detected = new IntentDetectionResult(IntentKind.PlatformQuestion, 0.9, "fr", "ai");
        _aiMock.Setup(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(detected);

        await Build().Handle(new ClassifyInboundIntentCommand(msg), _busMock.Object, CancellationToken.None);

        _contactsMock.Verify(x => x.UpdatePreferredLocaleAsync(
            msg.From.Value, "fr", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_AiReturnsGarbageLocale_PersistsFallbackEn()
    {
        // LocaleValidator.Sanitize folds anything that doesn't match the BCP-47
        // shape to "en" — protects the platform-answer prompt from injection.
        var detected = new IntentDetectionResult(IntentKind.PlatformQuestion, 0.9, "en\nINJECT", "ai");
        _aiMock.Setup(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(detected);

        await Build().Handle(new ClassifyInboundIntentCommand(Inbound("hi")), _busMock.Object, CancellationToken.None);

        _contactsMock.Verify(x => x.UpdatePreferredLocaleAsync(
            It.IsAny<string>(), "en", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_OuterCancellation_RethrowsAndDoesNotRoute()
    {
        // Adapter rethrows OCE when the outer token signalled — handler has no
        // catch, so the exception propagates to Wolverine's shutdown policy.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _aiMock.Setup(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => Build().Handle(
            new ClassifyInboundIntentCommand(Inbound("hi")), _busMock.Object, cts.Token));

        _routed.ShouldBeEmpty();
    }
}
