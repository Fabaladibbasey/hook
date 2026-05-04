using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Hook.UnitTests.Ai;

public class AiReplyHelperTests
{
    private static readonly ReplyContext Ctx =
        new("present-top-matches", RecentTurns: Array.Empty<ConversationTurn>(), LanguageHint: "en");

    [Fact]
    public async Task TryGenerateAsync_ShouldReturnReply_WhenAiSucceeds()
    {
        var ai = new ScriptedAi(reply: "Hi there");

        var result = await AiReplyHelper.TryGenerateAsync(ai, Ctx, "test", NullLogger.Instance, CancellationToken.None);

        result.ShouldBe("Hi there");
    }

    [Fact]
    public async Task TryGenerateAsync_ShouldReturnNull_WhenAiThrows()
    {
        var ai = new ScriptedAi(toThrow: new InvalidOperationException("ollama down"));

        var result = await AiReplyHelper.TryGenerateAsync(ai, Ctx, "test", NullLogger.Instance, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task TryGenerateAsync_ShouldReturnNull_WhenAiReturnsBlank()
    {
        var ai = new ScriptedAi(reply: "   ");

        var result = await AiReplyHelper.TryGenerateAsync(ai, Ctx, "test", NullLogger.Instance, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task TryGenerateAsync_ShouldRethrow_WhenCallerCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ai = new ScriptedAi(toThrow: new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            AiReplyHelper.TryGenerateAsync(ai, Ctx, "test", NullLogger.Instance, cts.Token));
    }

    [Theory]
    [InlineData("I'm sorry, but I can't assist with that.")]
    [InlineData("I cannot help with that request.")]
    [InlineData("Sorry, I can't do that.")]
    [InlineData("I'm just an AI and cannot provide that information.")]
    [InlineData("As an AI, I cannot fulfill this request.")]
    [InlineData("  i'm sorry, but i can't assist  ")]   // leading whitespace + lowercase
    [InlineData("Sorry, but I cannot help.")]
    public async Task TryGenerateAsync_ShouldReturnNull_WhenAiReturnsRefusal(string refusal)
    {
        var ai = new ScriptedAi(reply: refusal);

        var result = await AiReplyHelper.TryGenerateAsync(ai, Ctx, "test", NullLogger.Instance, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Theory]
    [InlineData("Sorry for the wait — here are your matches: 1) +1234")]   // "sorry" mid-sentence is fine
    [InlineData("I can fix that for you.")]
    [InlineData("Hi there! Let's get you sorted.")]
    public async Task TryGenerateAsync_ShouldPassThrough_WhenReplyContainsBenignSorry(string reply)
    {
        var ai = new ScriptedAi(reply: reply);

        var result = await AiReplyHelper.TryGenerateAsync(ai, Ctx, "test", NullLogger.Instance, CancellationToken.None);

        result.ShouldBe(reply);
    }

    private sealed class ScriptedAi : IConversationAi
    {
        private readonly string? _reply;
        private readonly Exception? _toThrow;

        public ScriptedAi(string? reply = null, Exception? toThrow = null)
        {
            _reply = reply;
            _toThrow = toThrow;
        }

        public Task<string> GenerateReplyAsync(ReplyContext context, CancellationToken ct = default)
        {
            if (_toThrow is not null) throw _toThrow;
            return Task.FromResult(_reply ?? string.Empty);
        }

        public Task<IntentDetectionResult> DetectIntentAsync(string userMessage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServiceExtractionResult> ExtractServicesAsync(string userMessage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServiceJudgeResult> JudgeServiceMatchAsync(string proposedSlug, IReadOnlyList<string> candidateSlugs, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LanguageDetectionResult> DetectLanguageAsync(string userMessage, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
