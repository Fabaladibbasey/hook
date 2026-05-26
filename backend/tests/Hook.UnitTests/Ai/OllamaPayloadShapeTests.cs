using System.Net;
using System.Text;
using System.Text.Json;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Hook.UnitTests.Ai;

public class OllamaPayloadShapeTests
{
    [Theory]
    [InlineData("intent", 60)]
    [InlineData("extract", 120)]
    [InlineData("judge", 60)]
    [InlineData("judgeParent", 60)]
    [InlineData("eta", 60)]
    [InlineData("reply", 200)]
    [InlineData("language", 30)]
    [InlineData("platformAnswer", 100)]
    public async Task EachTaskSendsNumPredict(string task, int expected)
    {
        var handler = new RecordingHandler(StubResponseFor(task));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        var ai = new OllamaConversationAi(
            http,
            Options.Create(new OllamaOptions()),
            Options.Create(new Hook.Features.Ai.PlatformQa.PlatformAnswerOptions()),
            NullLogger<OllamaConversationAi>.Instance);

        await InvokeTaskAsync(ai, task);

        handler.LastBody.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var numPredict = doc.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32();
        numPredict.ShouldBe(expected);
    }

    [Fact]
    public async Task BudgetIsHonouredFromOptions()
    {
        var handler = new RecordingHandler(StubResponseFor("reply"));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        var opts = new OllamaOptions { MaxOutputTokens = new OllamaTaskBudgets { Reply = 333 } };
        var ai = new OllamaConversationAi(
            http,
            Options.Create(opts),
            Options.Create(new Hook.Features.Ai.PlatformQa.PlatformAnswerOptions()),
            NullLogger<OllamaConversationAi>.Instance);

        await InvokeTaskAsync(ai, "reply");

        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32().ShouldBe(333);
    }

    private static Task InvokeTaskAsync(OllamaConversationAi ai, string task) => task switch
    {
        "intent" => ai.DetectIntentAsync("hi"),
        "extract" => ai.ExtractServicesAsync("plumber please"),
        "judge" => ai.JudgeServiceMatchAsync("plumbingg", ["plumbing", "carpentry"]),
        "judgeParent" => ai.JudgeParentSlugAsync("toilet-fix", ["home-services"], ["my toilet leaks"]),
        "eta" => ai.ExtractEtaAsync("tomorrow", DateTimeOffset.UtcNow),
        "reply" => ai.GenerateReplyAsync(new ReplyContext("greeting-reply", [], "en")),
        "language" => ai.DetectLanguageAsync("hello"),
        "platformAnswer" => ai.AnswerPlatformQuestionAsync("how does this work?", "en", "# overview\nHook is a bot."),
        _ => throw new ArgumentOutOfRangeException(nameof(task), task, "unknown task")
    };

    // Each call goes through ExtractMessageContent which reads `message.content` as a string.
    // For structured-output tasks the content is a JSON document the inner parser will read.
    private static string StubResponseFor(string task)
    {
        var content = task switch
        {
            "intent" => """{"intent":"Greeting","confidence":0.9,"language":"en"}""",
            "extract" => """{"slugs":["plumbing"]}""",
            "judge" => """{"matchedSlug":"plumbing","isNew":false}""",
            "judgeParent" => """{"parentSlug":"home-services"}""",
            "eta" => """{"etaUtc":null}""",
            "reply" => "hello",
            "language" => """{"language":"en","confidence":0.9}""",
            "platformAnswer" => "Hook is a WhatsApp bot.",
            _ => throw new ArgumentOutOfRangeException(nameof(task), task, "unknown task")
        };
        return "{\"message\":{\"content\":" + JsonSerializer.Serialize(content) + "}}";
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
