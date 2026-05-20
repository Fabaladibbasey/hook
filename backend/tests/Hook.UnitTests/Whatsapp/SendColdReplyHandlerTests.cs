using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Whatsapp.Phone;
using Hook.Features.Whatsapp.ReceiveWebhook.ColdReply;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.Whatsapp;

public class SendColdReplyHandlerTests
{
    private readonly Mock<IConversationAi> _aiMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly List<SendWhatsAppTextRequested> _sent = [];
    private ReplyContext? _capturedCtx;

    public SendColdReplyHandlerTests()
    {
        _busMock.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextRequested>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((msg, _) => _sent.Add((SendWhatsAppTextRequested)msg))
            .Returns(ValueTask.CompletedTask);
        _aiMock.Setup(x => x.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReplyContext ctx, CancellationToken _) =>
            {
                _capturedCtx = ctx;
                return "ai-reply";
            });
    }

    private SendColdReplyHandler Build() =>
        new(_aiMock.Object, _busMock.Object, NullLogger<SendColdReplyHandler>.Instance);

    private static PhoneNumber To() => PhoneNumber.Parse("+220300001");

    [Fact]
    public async Task Handle_Greeting_AiHappyPath_UsesGreetingPurposeAndSendsAiReply()
    {
        var detected = new IntentDetectionResult(IntentKind.Greeting, 0.99, "en", "ai");

        await Build().Handle(
            new SendColdReplyRequested(To(), "hi", detected, "greeting-reply"), CancellationToken.None);

        _capturedCtx.ShouldNotBeNull();
        _capturedCtx!.Purpose.ShouldBe("greeting-reply");
        _capturedCtx.Facts!["intent"].ShouldBe(IntentKind.Greeting.ToString());
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldBe("ai-reply");
    }

    [Fact]
    public async Task Handle_OutOfScope_AiFails_FallsBackToOutOfScopeText()
    {
        _aiMock.Setup(x => x.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("ollama down"));
        var detected = new IntentDetectionResult(IntentKind.Unknown, 0.2, "en", "ai");

        await Build().Handle(
            new SendColdReplyRequested(To(), "asdf", detected, "out-of-scope"), CancellationToken.None);

        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("connect people who need services");
    }

    [Fact]
    public async Task Handle_Greeting_AiFails_FallsBackToGreetingText()
    {
        _aiMock.Setup(x => x.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("ollama down"));
        var detected = new IntentDetectionResult(IntentKind.Greeting, 0.95, "en", "ai");

        await Build().Handle(
            new SendColdReplyRequested(To(), "hi", detected, "greeting-reply"), CancellationToken.None);

        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("I connect people with local service providers");
    }
}
