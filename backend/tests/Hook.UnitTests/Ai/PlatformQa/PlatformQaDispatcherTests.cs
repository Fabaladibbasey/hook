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
    private readonly Mock<IPlatformAnswerDedupGate> _dedupMock = new();
    private readonly List<object> _published = [];

    public PlatformQaDispatcherTests()
    {
        _bus.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((m, _) => _published.Add(m))
            .Returns(ValueTask.CompletedTask);
        _bus.Setup(x => x.PublishAsync(It.IsAny<AnswerPlatformQuestionCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((m, _) => _published.Add(m))
            .Returns(ValueTask.CompletedTask);
        // Default to "claim wins" so existing tests exercise the send path.
        _dedupMock.Setup(x => x.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private PlatformQaDispatcher Build() =>
        new(_bus.Object, Options.Create(new FeedbackOptions()), _dedupMock.Object);

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

    // Caller-en override: cold-deterministic callers pin locale="en" before any
    // language signal has been detected, so the phrase-detected locale wins.
    [Theory]
    [InlineData("qui es-tu?", "en", "Je suis Hook")]
    [InlineData("c'est quoi?", "en", "Je suis Hook")]
    [InlineData("quien eres?", "en", "Soy Hook")]
    [InlineData("qué es esto?", "en", "Soy Hook")]
    [InlineData("o que é isto?", "en", "Sou o Hook")]
    [InlineData("quem és você?", "en", "Sou o Hook")]
    [InlineData("ما هذا؟", "en", "أنا Hook")]          // Arabic question mark
    [InlineData("من انت؟", "en", "أنا Hook")]
    [InlineData("yan nga tudd", "en", "Maa di Hook")]
    [InlineData("ko honɗun", "en", "I'm Hook")]        // Fula — TODO: native review
    [InlineData("muna le ñin", "en", "I'm Hook")]      // Mandinka — TODO: native review
    public async Task DispatchColdAsync_NonEnglishIdentity_OverridesEnglishCallerLocale(
        string text, string callerLocale, string startsWith)
    {
        await Build().DispatchColdAsync(To(), text, callerLocale, "cold-deterministic");

        var send = _published[0].ShouldBeOfType<SendWhatsAppTextCommand>();
        send.Text.ShouldStartWith(startsWith);
    }

    // Caller-supplied non-en locale (from the LLM classifier path) wins because
    // the classifier had real signal — we don't second-guess it from the phrase.
    [Fact]
    public async Task DispatchColdAsync_CallerLocaleNonEn_PreservedOverPhraseDetection()
    {
        await Build().DispatchColdAsync(To(), "what is this?", "fr", "cold-deterministic");

        var send = _published[0].ShouldBeOfType<SendWhatsAppTextCommand>();
        send.Text.ShouldStartWith("Je suis Hook");
    }

    [Fact]
    public void Normalize_StripsWhitespaceTrailingQuestionMarkAndLowers()
    {
        PlatformQaDispatcher.Normalize("  What  Is   This??  ").ShouldBe("what is this");
        PlatformQaDispatcher.Normalize("").ShouldBe(string.Empty);
        PlatformQaDispatcher.Normalize("   ").ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("ما هذا؟", "ما هذا")]                  // Arabic question mark trim
    [InlineData("ما هذا؟؟", "ما هذا")]
    [InlineData("qué es esto?", "qué es esto")]
    public void Normalize_HandlesArabicAndAccentedScripts(string input, string expected)
        => PlatformQaDispatcher.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData("c’est quoi", "c'est quoi")]
    [InlineData("what’s this", "what's this")]
    [InlineData("‘what is this’", "'what is this'")]
    public void Normalize_CanonicalisesCurlyApostrophe(string input, string expected) =>
        PlatformQaDispatcher.Normalize(input).ShouldBe(expected);

    [Fact]
    public void Normalize_FoldsZeroWidthSpace()
    {
        // U+200B between tokens should collapse via whitespace split.
        PlatformQaDispatcher.Normalize("what​ is​ this").ShouldBe("what is this");
    }

    [Fact]
    public async Task DispatchColdAsync_IdentityShortcut_DedupSuppressed_NoSecondPublish()
    {
        _dedupMock.Setup(x => x.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await Build().DispatchColdAsync(To(), "what is this?", "en", "cold-deterministic");

        _published.ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchColdAsync_IdentityPhrase_CurlyApostrophe_StillMatches()
    {
        // iOS / Android autocorrect emits U+2019. Normalize folds to ASCII '.
        await Build().DispatchColdAsync(To(), "what’s hook", "en", "cold-deterministic");

        var send = _published.ShouldHaveSingleItem().ShouldBeOfType<SendWhatsAppTextCommand>();
        send.Text.ShouldStartWith("I'm Hook —");
    }

    [Theory]
    [InlineData("EN")]   // case-insens en
    [InlineData("En")]
    [InlineData("EN-us")]
    public async Task DispatchColdAsync_IdentityPhrase_CaseInsensEnglishLocale_OverriddenByPhraseLocale(
        string callerLocale)
    {
        await Build().DispatchColdAsync(To(), "qui es-tu?", callerLocale, "cold-deterministic");

        var send = _published.ShouldHaveSingleItem().ShouldBeOfType<SendWhatsAppTextCommand>();
        send.Text.ShouldStartWith("Je suis Hook");
    }

    [Theory]
    [InlineData("mnk")]
    [InlineData("ff")]
    public void IdentityReplyFor_ThreeLetterLocale_ReachesDictionarySlot(string locale)
    {
        // mnk + ff slots currently map to IdentityReplyEn (TODO: native translations).
        // The test pins that LocalisedString.For *reaches* the slot rather than
        // falling back via 2-letter truncation (mnk → mn → miss → default).
        PlatformQaDispatcher.IdentityReplyForTest(locale).ShouldStartWith("I'm Hook —");
    }

    [Theory]
    [InlineData("ｗｈａｔ ｉｓ ｔｈｉｓ", "what is this")] // full-width letters → ASCII via NFKC
    [InlineData("whoـareـyou",           "who are you")]   // Arabic tatweel U+0640 stripped
    [InlineData("who‌are‍you",  "who are you")]   // ZWNJ+ZWJ replaced with spaces
    public void Normalize_InvisibleUnicodeVariants_CanonicalisesToAscii(string input, string expected)
    {
        PlatformQaDispatcher.Normalize(input).ShouldBe(expected);
    }
}
