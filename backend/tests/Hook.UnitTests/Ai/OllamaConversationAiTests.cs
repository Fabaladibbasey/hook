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

    private static OllamaConversationAi BuildAi(string ollamaContent)
    {
        var handler = new ScriptedHandler(content: ollamaContent);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var options = Options.Create(new OllamaOptions { Model = "test-model" });
        return new OllamaConversationAi(http, options, NullLogger<OllamaConversationAi>.Instance);
    }

    private static ReplyContext ReplyCtx(string purpose) =>
        new(purpose, RecentTurns: [], LanguageHint: "en");

    private sealed class ScriptedHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var encoded = System.Text.Json.JsonSerializer.Serialize(content);
            var json = "{\"message\":{\"content\":" + encoded + "}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
