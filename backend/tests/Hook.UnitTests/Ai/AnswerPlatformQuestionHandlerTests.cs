using System.Diagnostics.Metrics;
using Hook.Features.Ai;
using Hook.Features.Ai.PlatformQa;
using Hook.Features.Observability;
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
        // Headers are stripped on KB load; assert on prose unique to the overview section.
        capturedKb!.ShouldContain("WhatsApp-first marketplace");
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
    public async Task Handle_DedupClaimedAfterSuccessfulPublish_NotBefore()
    {
        var claimOrder = 0;
        var publishOrder = 0;
        var seq = 0;

        _aiMock.Setup(x => x.AnswerPlatformQuestionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Hook is free.");

        _busMock.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback(() => publishOrder = ++seq)
            .Returns(ValueTask.CompletedTask);
        _dedupMock.Setup(x => x.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Callback(() => claimOrder = ++seq)
            .ReturnsAsync(true);

        await Build().Handle(
            new AnswerPlatformQuestionCommand(To(), "is this free?", "en", "cold-deterministic"),
            CancellationToken.None);

        publishOrder.ShouldBe(1);
        claimOrder.ShouldBe(2);
        _busMock.Verify(x => x.PublishAsync(
                It.IsAny<SendWhatsAppTextCommand>(), It.IsAny<DeliveryOptions>()),
            Times.Once);
        _dedupMock.Verify(x => x.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_DedupClaimThrowsAfterPublish_DoesNotFaultHandler_NoDuplicateAnswer()
    {
        _aiMock.Setup(x => x.AnswerPlatformQuestionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Hook is free.");
        _dedupMock.Setup(x => x.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db blip"));

        // Must NOT throw — a Wolverine retry would re-run a 60-150s Ollama
        // inference AND re-publish a duplicate WhatsApp text to the user.
        await Build().Handle(
            new AnswerPlatformQuestionCommand(To(), "is this free?", "en", "cold-deterministic"),
            CancellationToken.None);

        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldBe("Hook is free.");
    }

    [Fact]
    public async Task Handle_DedupReturnsFalse_AfterPublish_AiCostSuppressionMetricFires()
    {
        // Dedup row already present (prior recent answer) — TryClaim returns
        // false. We've already published; the post-publish claim is a no-op
        // for delivery semantics. Only fires on a Wolverine retry of the same
        // envelope — the pre-publish gate is gone. Assert the metric tag so
        // a regression that swallows the dedup-row-present signal is visible.
        var captured = new List<(long Value, string Stage, string Reason)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == HookMetrics.MeterName
                && instrument.Name == "hook.ai.outbound_dropped")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            string stage = string.Empty, reason = string.Empty;
            foreach (var kv in tags)
            {
                if (kv.Key == "stage") stage = kv.Value?.ToString() ?? string.Empty;
                if (kv.Key == "reason") reason = kv.Value?.ToString() ?? string.Empty;
            }
            captured.Add((measurement, stage, reason));
        });
        listener.Start();

        _aiMock.Setup(x => x.AnswerPlatformQuestionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Hook is free.");
        _dedupMock.Setup(x => x.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await Build().Handle(
            new AnswerPlatformQuestionCommand(To(), "is this free?", "en", "cold-deterministic"),
            CancellationToken.None);

        _sent.ShouldHaveSingleItem();
        captured.ShouldContain(m => m.Stage == "platform-answer" && m.Reason == "dedup-row-present");
    }

    [Fact]
    public async Task Handle_DedupClaimThrowsOce_PostPublish_DoesNotFaultHandler()
    {
        // OCE from the dedup persist must not escape — Wolverine would retry
        // the envelope, re-running a 60-150s Ollama inference AND publishing
        // a duplicate WhatsApp text to the user. The previous
        // `when (ex is not OCE)` filter let it escape.
        _aiMock.Setup(x => x.AnswerPlatformQuestionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Hook is free.");
        _dedupMock.Setup(x => x.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Build().Handle(
            new AnswerPlatformQuestionCommand(To(), "is this free?", "en", "cold-deterministic"),
            CancellationToken.None);

        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldBe("Hook is free.");
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
