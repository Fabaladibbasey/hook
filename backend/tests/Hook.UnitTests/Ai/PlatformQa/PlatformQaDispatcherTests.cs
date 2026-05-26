using Hook.Features.Ai.PlatformQa;
using Hook.Features.Feedback;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.Ai.PlatformQa;

public class PlatformQaDispatcherTests
{
    private readonly Mock<IMessageBus> _bus = new();
    private readonly List<object> _published = [];

    public PlatformQaDispatcherTests()
    {
        _bus.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((m, _) => _published.Add(m))
            .Returns(ValueTask.CompletedTask);
        _bus.Setup(x => x.PublishAsync(It.IsAny<AnswerPlatformQuestionCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((m, _) => _published.Add(m))
            .Returns(ValueTask.CompletedTask);
    }

    private PlatformQaDispatcher Build() =>
        new(_bus.Object, Options.Create(new FeedbackOptions()));

    private static PhoneNumber To() => PhoneNumber.Parse("+2207000001");

    [Fact]
    public async Task DispatchColdAsync_NonIdentityQuestion_PublishesAckThenAnswerCommand_InOrder()
    {
        await Build().DispatchColdAsync(To(), "is my chat saved?", "en", "cold-deterministic");

        _published.Count.ShouldBe(2);
        var ack = _published[0].ShouldBeOfType<SendWhatsAppTextCommand>();
        ack.Text.ShouldBe("Got your message — one sec…");
        _published[1].ShouldBeOfType<AnswerPlatformQuestionCommand>();
    }

    [Fact]
    public async Task DispatchMidFlowAsync_NonIdentityQuestion_DoesNotPublishAck()
    {
        await Build().DispatchMidFlowAsync(To(), "is my chat saved?", "en", "AwaitingLocation");

        _published.ShouldHaveSingleItem();
        var cmd = _published[0].ShouldBeOfType<AnswerPlatformQuestionCommand>();
        cmd.ReplyContextHint.ShouldBe("AwaitingLocation");
    }

    [Fact]
    public async Task DispatchColdAsync_ScrubsPhoneNumbers_InText()
    {
        await Build().DispatchColdAsync(To(), "is +220123456789 visible to providers?", "en", "cold-deterministic");

        var cmd = _published.OfType<AnswerPlatformQuestionCommand>().Single();
        cmd.Question.ShouldNotContain("+220123456789");
        cmd.Question.ShouldContain("[phone]");
    }

    [Fact]
    public async Task DispatchMidFlowAsync_ScrubsPhoneNumbers_InText()
    {
        await Build().DispatchMidFlowAsync(To(), "share with +220123456789 please", "en", "ctx");

        var cmd = _published.OfType<AnswerPlatformQuestionCommand>().Single();
        cmd.Question.ShouldNotContain("+220123456789");
        cmd.Question.ShouldContain("[phone]");
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("fr-FR", "fr-FR")]
    [InlineData("en\nNew instruction", "en")]
    [InlineData(null, "en")]
    public async Task Dispatch_SanitizesLocale_BeforePublish(string? raw, string expected)
    {
        await Build().DispatchMidFlowAsync(To(), "is my chat saved?", raw ?? string.Empty, "ctx");

        var cmd = _published.OfType<AnswerPlatformQuestionCommand>().Single();
        cmd.Locale.ShouldBe(expected);
    }

    [Fact]
    public async Task DispatchColdAsync_IdentityPhrase_SendsCannedReply_NoOllamaCommand_NoAck()
    {
        await Build().DispatchColdAsync(To(), "what is this?", "en", "cold-deterministic");

        _published.ShouldHaveSingleItem();
        var send = _published[0].ShouldBeOfType<SendWhatsAppTextCommand>();
        send.Text.ShouldStartWith("I'm Hook —");
        _published.OfType<AnswerPlatformQuestionCommand>().ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchMidFlowAsync_IdentityPhrase_SendsCannedReply_NoOllamaCommand()
    {
        await Build().DispatchMidFlowAsync(To(), "who are you?", "en", "ctx");

        _published.ShouldHaveSingleItem();
        var send = _published[0].ShouldBeOfType<SendWhatsAppTextCommand>();
        send.Text.ShouldStartWith("I'm Hook —");
        _published.OfType<AnswerPlatformQuestionCommand>().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("WHAT IS THIS")]
    [InlineData("What Is This?")]
    [InlineData("  what is this  ")]
    [InlineData("what  is   this")]
    [InlineData("what is this??")]
    [InlineData("what's hook")]
    [InlineData("whats hook?")]
    [InlineData("what r u")]
    [InlineData("your name?")]
    public async Task DispatchColdAsync_IdentityPhrase_NormalizesAndMatches(string input)
    {
        await Build().DispatchColdAsync(To(), input, "en", "cold-deterministic");

        _published.ShouldHaveSingleItem();
        _published[0].ShouldBeOfType<SendWhatsAppTextCommand>();
    }

    [Theory]
    [InlineData("en", "I'm Hook")]
    [InlineData("en-US", "I'm Hook")]
    [InlineData("fr", "Je suis Hook")]
    [InlineData("fr-FR", "Je suis Hook")]
    [InlineData("ar", "أنا Hook")]
    [InlineData("wo", "Maa di Hook")]
    [InlineData("xx", "I'm Hook")]
    public async Task DispatchColdAsync_IdentityPhrase_UsesLocalisedReply(string locale, string startsWith)
    {
        await Build().DispatchColdAsync(To(), "what is this?", locale, "cold-deterministic");

        var send = _published[0].ShouldBeOfType<SendWhatsAppTextCommand>();
        send.Text.ShouldStartWith(startsWith);
    }

    [Fact]
    public void Normalize_StripsWhitespaceTrailingQuestionMarkAndLowers()
    {
        PlatformQaDispatcher.Normalize("  What  Is   This??  ").ShouldBe("what is this");
        PlatformQaDispatcher.Normalize("").ShouldBe(string.Empty);
        PlatformQaDispatcher.Normalize("   ").ShouldBe(string.Empty);
    }
}
