using System.Net;
using System.Text;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback.Step1Intent;
using Hook.Features.Feedback.Step2Intent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Hook.UnitTests.Ai;

public class OllamaConversationAiTests
{
    [Fact]
    public async Task GenerateReplyAsync_ShouldThrowAiEmptyReplyException_WhenModelReturnsBlank()
    {
        var ai = BuildAi(ollamaContent: "");

        var ctx = ReplyCtx("present-top-matches");

        await Should.ThrowAsync<AiEmptyReplyException>(() => ai.GenerateReplyAsync(ctx));
    }

    [Fact]
    public async Task GenerateReplyAsync_ShouldThrowAiEmptyReplyException_WhenModelReturnsWhitespace()
    {
        var ai = BuildAi(ollamaContent: "   \n\t  ");

        var ctx = ReplyCtx("feedback-step-1-did-you-find");

        var ex = await Should.ThrowAsync<AiEmptyReplyException>(() => ai.GenerateReplyAsync(ctx));
        ex.Purpose.ShouldBe("feedback-step-1-did-you-find");
    }

    [Fact]
    public async Task GenerateReplyAsync_ShouldReturnContent_WhenModelReturnsText()
    {
        var ai = BuildAi(ollamaContent: "Hello world");

        var ctx = ReplyCtx("present-top-matches");

        var reply = await ai.GenerateReplyAsync(ctx);

        reply.ShouldBe("Hello world");
    }

    [Fact]
    public async Task DetectIntentAsync_ReturnsNeutralUnknown_OnTransportFailure()
    {
        var ai = BuildFailingAi();

        var result = await ai.DetectIntentAsync("anything");

        result.Intent.ShouldBe(IntentKind.Unknown);
        result.Confidence.ShouldBe(0);
        result.LanguageCode.ShouldBe("en");
        result.Notes.ShouldBe("exception");
    }

    [Fact]
    public async Task ExtractServicesAsync_ReturnsEmpty_OnTransportFailure()
    {
        var ai = BuildFailingAi();

        var result = await ai.ExtractServicesAsync("anything");

        result.Slugs.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExtractEtaAsync_ReturnsNull_OnTransportFailure()
    {
        var ai = BuildFailingAi();

        var result = await ai.ExtractEtaAsync("in 3 hours", DateTimeOffset.UtcNow);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task JudgeParentSlugAsync_ReturnsNull_OnTransportFailure()
    {
        var ai = BuildFailingAi();

        var result = await ai.JudgeParentSlugAsync("cardiology", ["doctor"], []);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task JudgeServiceMatchAsync_ReturnsAssumeNew_OnTransportFailure()
    {
        var ai = BuildFailingAi();

        var result = await ai.JudgeServiceMatchAsync("plumbing", ["pipe-repair", "drainage"]);

        result.MatchedSlug.ShouldBe(string.Empty);
        result.IsNew.ShouldBeTrue();
        result.ProposedSlug.ShouldBe("plumbing");
    }

    [Fact]
    public async Task PingAsync_Throws_OnTransportFailure()
    {
        // Probe must NOT absorb — /readyz + warmup rely on the throw to tell
        // "healthy" apart from "Ollama unreachable".
        var ai = BuildFailingAi();

        await Should.ThrowAsync<HttpRequestException>(() => ai.PingAsync());
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("extract")]
    [InlineData("judge-match")]
    [InlineData("judge-parent")]
    [InlineData("eta")]
    public async Task WrappedMethods_PropagateOce_WhenOuterTokenCancelled(string method)
    {
        // Transport throws OCE because the outer token signalled — the adapter
        // must rethrow so Wolverine's shutdown OCE policy can discard cleanly.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ai = BuildAi(new CancellingHandler());

        await Should.ThrowAsync<OperationCanceledException>(() => method switch
        {
            "intent" => ai.DetectIntentAsync("anything", cts.Token),
            "extract" => ai.ExtractServicesAsync("anything", cts.Token),
            "judge-match" => ai.JudgeServiceMatchAsync("plumbing", ["pipe-repair"], cts.Token),
            "judge-parent" => ai.JudgeParentSlugAsync("cardiology", ["doctor"], [], cts.Token),
            "eta" => ai.ExtractEtaAsync("in 3 hours", DateTimeOffset.UtcNow, cts.Token),
            _ => throw new InvalidOperationException(method)
        });
    }

    [Fact]
    public async Task GenerateReplyAsync_Propagates_OnTransportFailure()
    {
        // GenerateReply is NOT absorbed — callers route via AiReplyHelper.
        var ai = BuildFailingAi();

        await Should.ThrowAsync<HttpRequestException>(() =>
            ai.GenerateReplyAsync(ReplyCtx("present-top-matches")));
    }

    [Fact]
    public async Task DetectLanguageAsync_Propagates_OnTransportFailure()
    {
        var ai = BuildFailingAi();

        await Should.ThrowAsync<HttpRequestException>(() => ai.DetectLanguageAsync("hola"));
    }

    // -- ExtractStep1IntentAsync --

    [Theory]
    [InlineData("Yes", Step1ReplyIntent.Yes)]
    [InlineData("yes", Step1ReplyIntent.Yes)]
    [InlineData("YES", Step1ReplyIntent.Yes)]
    [InlineData("No", Step1ReplyIntent.No)]
    [InlineData("reschedule", Step1ReplyIntent.Reschedule)]
    [InlineData("STOPASKING", Step1ReplyIntent.StopAsking)]
    [InlineData("garbage", Step1ReplyIntent.Unclear)]
    [InlineData("", Step1ReplyIntent.Unclear)]
    public async Task ExtractStep1IntentAsync_CaseInsensitiveIntent_MapsToEnum(string raw, Step1ReplyIntent expected)
    {
        var ai = BuildStep1Ai(intent: raw, etaUtc: null);

        var result = await ai.ExtractStep1IntentAsync("anything", DateTimeOffset.UtcNow);

        result.Intent.ShouldBe(expected);
        result.Eta.ShouldBeNull();
    }

    [Fact]
    public async Task ExtractStep1IntentAsync_RescheduleWithFutureEta_ParsesEta()
    {
        var now = DateTimeOffset.Parse("2026-05-24T12:00:00Z");
        var eta = "2026-05-25T12:00:00Z";
        var ai = BuildStep1Ai(intent: "Reschedule", etaUtc: eta);

        var result = await ai.ExtractStep1IntentAsync("tomorrow", now);

        result.Intent.ShouldBe(Step1ReplyIntent.Reschedule);
        result.Eta.ShouldBe(DateTimeOffset.Parse(eta));
    }

    [Fact]
    public async Task ExtractStep1IntentAsync_ReschedulePastEta_DropsEta()
    {
        var now = DateTimeOffset.Parse("2026-05-24T12:00:00Z");
        var ai = BuildStep1Ai(intent: "Reschedule", etaUtc: "2026-05-23T12:00:00Z");

        var result = await ai.ExtractStep1IntentAsync("yesterday", now);

        result.Intent.ShouldBe(Step1ReplyIntent.Reschedule);
        result.Eta.ShouldBeNull();
    }

    [Fact]
    public async Task ExtractStep1IntentAsync_RescheduleUnparseableEta_DropsEta()
    {
        var ai = BuildStep1Ai(intent: "Reschedule", etaUtc: "not-a-date");

        var result = await ai.ExtractStep1IntentAsync("soon", DateTimeOffset.UtcNow);

        result.Intent.ShouldBe(Step1ReplyIntent.Reschedule);
        result.Eta.ShouldBeNull();
    }

    [Fact]
    public async Task ExtractStep1IntentAsync_NonRescheduleWithEta_DiscardsEta()
    {
        var ai = BuildStep1Ai(intent: "Yes", etaUtc: "2099-01-01T00:00:00Z");

        var result = await ai.ExtractStep1IntentAsync("yes", DateTimeOffset.UtcNow);

        result.Intent.ShouldBe(Step1ReplyIntent.Yes);
        result.Eta.ShouldBeNull();
    }

    [Fact]
    public async Task ExtractStep1IntentAsync_TransportFailure_ReturnsUnclearFallback()
    {
        var ai = BuildFailingAi();

        var result = await ai.ExtractStep1IntentAsync("anything", DateTimeOffset.UtcNow);

        result.Intent.ShouldBe(Step1ReplyIntent.Unclear);
        result.Eta.ShouldBeNull();
    }

    private static OllamaConversationAi BuildStep1Ai(string intent, string? etaUtc)
    {
        // The handler wraps a JSON object inside the Ollama message envelope.
        var inner = etaUtc is null
            ? $"{{\"intent\":\"{intent}\",\"etaUtc\":null}}"
            : $"{{\"intent\":\"{intent}\",\"etaUtc\":\"{etaUtc}\"}}";
        return BuildAi(inner);
    }

    // -- ExtractStep2IntentAsync --

    [Theory]
    [InlineData("Yes", Step2ReplyIntent.Yes)]
    [InlineData("no", Step2ReplyIntent.No)]
    [InlineData("INPROGRESS", Step2ReplyIntent.InProgress)]
    [InlineData("StopAsking", Step2ReplyIntent.StopAsking)]
    [InlineData("in_progress", Step2ReplyIntent.Unclear)]
    [InlineData("garbage", Step2ReplyIntent.Unclear)]
    [InlineData("", Step2ReplyIntent.Unclear)]
    public async Task ExtractStep2IntentAsync_CaseInsensitiveIntent_MapsToEnum(string raw, Step2ReplyIntent expected)
    {
        var ai = BuildStep2Ai(intent: raw, etaUtc: null);

        var result = await ai.ExtractStep2IntentAsync("anything", DateTimeOffset.UtcNow);

        result.Intent.ShouldBe(expected);
        result.Eta.ShouldBeNull();
    }

    [Fact]
    public async Task ExtractStep2IntentAsync_InProgressWithFutureEta_ParsesEta()
    {
        var now = DateTimeOffset.Parse("2026-05-24T12:00:00Z");
        var eta = "2026-05-24T15:00:00Z";
        var ai = BuildStep2Ai(intent: "InProgress", etaUtc: eta);

        var result = await ai.ExtractStep2IntentAsync("in 3 hours", now);

        result.Intent.ShouldBe(Step2ReplyIntent.InProgress);
        result.Eta.ShouldBe(DateTimeOffset.Parse(eta));
    }

    [Fact]
    public async Task ExtractStep2IntentAsync_InProgressPastEta_DropsEta()
    {
        var now = DateTimeOffset.Parse("2026-05-24T12:00:00Z");
        var ai = BuildStep2Ai(intent: "InProgress", etaUtc: "2026-05-23T12:00:00Z");

        var result = await ai.ExtractStep2IntentAsync("in 3 hours", now);

        result.Intent.ShouldBe(Step2ReplyIntent.InProgress);
        result.Eta.ShouldBeNull();
    }

    [Fact]
    public async Task ExtractStep2IntentAsync_InProgressUnparseableEta_DropsEta()
    {
        var ai = BuildStep2Ai(intent: "InProgress", etaUtc: "not-a-date");

        var result = await ai.ExtractStep2IntentAsync("in a while", DateTimeOffset.UtcNow);

        result.Intent.ShouldBe(Step2ReplyIntent.InProgress);
        result.Eta.ShouldBeNull();
    }

    [Fact]
    public async Task ExtractStep2IntentAsync_NonInProgressWithEta_DiscardsEta()
    {
        var ai = BuildStep2Ai(intent: "Yes", etaUtc: "2099-01-01T00:00:00Z");

        var result = await ai.ExtractStep2IntentAsync("yes", DateTimeOffset.UtcNow);

        result.Intent.ShouldBe(Step2ReplyIntent.Yes);
        result.Eta.ShouldBeNull();
    }

    [Fact]
    public async Task ExtractStep2IntentAsync_TransportFailure_ReturnsUnclearFallback()
    {
        var ai = BuildFailingAi();

        var result = await ai.ExtractStep2IntentAsync("anything", DateTimeOffset.UtcNow);

        result.Intent.ShouldBe(Step2ReplyIntent.Unclear);
        result.Eta.ShouldBeNull();
    }

    // T5: server-side ETA guard — AI cannot inject etaUtc when source text has
    // no digit run and no ETA keyword.
    [Fact]
    public async Task ExtractStep2IntentAsync_AiFabricatesEtaForGarbageText_DropsEta()
    {
        var ai = BuildStep2Ai(intent: "InProgress", etaUtc: "2099-01-01T00:00:00Z");

        var result = await ai.ExtractStep2IntentAsync("blegh xyz", DateTimeOffset.UtcNow);

        result.Intent.ShouldBe(Step2ReplyIntent.InProgress);
        result.Eta.ShouldBeNull();
    }

    [Fact]
    public async Task ExtractStep1IntentAsync_AiFabricatesEtaForGarbageText_DropsEta()
    {
        var ai = BuildStep1Ai(intent: "Reschedule", etaUtc: "2099-01-01T00:00:00Z");

        var result = await ai.ExtractStep1IntentAsync("blegh xyz", DateTimeOffset.UtcNow);

        result.Intent.ShouldBe(Step1ReplyIntent.Reschedule);
        result.Eta.ShouldBeNull();
    }

    private static OllamaConversationAi BuildStep2Ai(string intent, string? etaUtc)
    {
        var inner = etaUtc is null
            ? $"{{\"intent\":\"{intent}\",\"etaUtc\":null}}"
            : $"{{\"intent\":\"{intent}\",\"etaUtc\":\"{etaUtc}\"}}";
        return BuildAi(inner);
    }

    private static OllamaConversationAi BuildAi(string ollamaContent)
    {
        var handler = new ScriptedHandler(content: ollamaContent);
        return BuildAi(handler);
    }

    private static OllamaConversationAi BuildFailingAi() => BuildAi(new FailingHandler());

    private static OllamaConversationAi BuildAi(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var options = Options.Create(new OllamaOptions { Model = "test-model" });
        return new OllamaConversationAi(http, options, NullLogger<OllamaConversationAi>.Instance);
    }

    private static ReplyContext ReplyCtx(string purpose) =>
        new(purpose, RecentTurns: [], LanguageHint: "en");

    private sealed class ScriptedHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var encoded = System.Text.Json.JsonSerializer.Serialize(content);
            var json = "{\"message\":{\"content\":" + encoded + "}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("ollama unreachable");
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
