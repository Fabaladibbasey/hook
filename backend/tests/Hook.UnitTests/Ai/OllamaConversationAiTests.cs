using System.Net;
using System.Text;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
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
