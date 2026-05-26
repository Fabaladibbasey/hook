using Hook.Features.Ai;
using Hook.Features.Ai.PlatformQa;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.Ai;

public class AnswerPlatformQuestionHandlerTests
{
    private readonly Mock<IConversationAi> _aiMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly Mock<IPlatformAnswerDedupGate> _dedupMock = new();
    private readonly List<SendWhatsAppTextCommand> _sent = [];
    private readonly PlatformKnowledgeBase _kb =
        new(Options.Create(new PlatformKnowledgeBaseOptions()));

    public AnswerPlatformQuestionHandlerTests()
    {
        _busMock.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((msg, _) => _sent.Add((SendWhatsAppTextCommand)msg))
            .Returns(ValueTask.CompletedTask);
        // Default to "claimed" so existing tests exercise the AI path.
        _dedupMock.Setup(x => x.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private AnswerPlatformQuestionHandler Build() =>
        new(_aiMock.Object, _kb, _dedupMock.Object, _busMock.Object,
            NullLogger<AnswerPlatformQuestionHandler>.Instance);

    private static PhoneNumber To() => PhoneNumber.Parse("+2207000001");

    [Fact]
    public async Task Handle_AiReturnsText_PublishesItVerbatim()
    {
        _aiMock.Setup(x => x.AnswerPlatformQuestionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Hook is free during launch.");

        await Build().Handle(
            new AnswerPlatformQuestionCommand(To(), "is this free?", "en", "cold-deterministic"),
            CancellationToken.None);

        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldBe("Hook is free during launch.");
    }

    [Fact]
    public async Task Handle_AiReturnsNull_PublishesDeterministicFallback()
    {
        _aiMock.Setup(x => x.AnswerPlatformQuestionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await Build().Handle(
            new AnswerPlatformQuestionCommand(To(), "how does this work?", "en", "cold-classified"),
            CancellationToken.None);

        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldBe(AnswerPlatformQuestionHandler.Fallback);
    }

    [Fact]
    public async Task Handle_AiReturnsWhitespace_TreatedAsFallback()
    {
        _aiMock.Setup(x => x.AnswerPlatformQuestionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("   \n  ");

        await Build().Handle(
            new AnswerPlatformQuestionCommand(To(), "what?", "en", "mid-flow:AwaitingLocation"),
            CancellationToken.None);

        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldBe(AnswerPlatformQuestionHandler.Fallback);
    }

    [Fact]
    public async Task Handle_ForwardsKbContentToAi()
    {
        string? capturedKb = null;
        _aiMock.Setup(x => x.AnswerPlatformQuestionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, kb, _) => capturedKb = kb)
            .ReturnsAsync("ok");

        await Build().Handle(
            new AnswerPlatformQuestionCommand(To(), "what's hook?", "en", "cold-deterministic"),
            CancellationToken.None);

        capturedKb.ShouldNotBeNull();
        capturedKb!.ShouldContain("# What Hook is");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("xx")]   // unknown locale → English fallback
    public async Task Handle_NullReply_EnglishOrUnknownLocale_PublishesEnglishFallback(string locale)
    {
        _aiMock.Setup(x => x.AnswerPlatformQuestionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await Build().Handle(
            new AnswerPlatformQuestionCommand(To(), "huh?", locale, "cold-deterministic"),
            CancellationToken.None);

        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldBe(AnswerPlatformQuestionHandler.Fallback);
    }

    [Fact]
    public async Task Handle_DedupRejects_ShortCircuits_NoAiCall_NoOutboundPublish()
    {
        _dedupMock.Setup(x => x.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await Build().Handle(
            new AnswerPlatformQuestionCommand(To(), "what's hook?", "en", "cold-deterministic"),
            CancellationToken.None);

        _sent.ShouldBeEmpty();
        _aiMock.Verify(x => x.AnswerPlatformQuestionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_NullReply_FrenchLocale_PublishesFrenchFallback()
    {
        _aiMock.Setup(x => x.AnswerPlatformQuestionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await Build().Handle(
            new AnswerPlatformQuestionCommand(To(), "quoi?", "fr", "cold-deterministic"),
            CancellationToken.None);

        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldStartWith("Je ne suis pas sûr");
    }
}
