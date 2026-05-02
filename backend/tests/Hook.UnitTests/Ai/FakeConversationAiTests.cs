using Hook.Features.Ai.Models;
using Hook.TestHelpers;
using Shouldly;

namespace Hook.UnitTests.Ai;

public class FakeConversationAiTests
{
    private readonly FakeConversationAi _ai = new();

    [Theory]
    [InlineData("yes", IntentKind.Confirmation)]
    [InlineData("no thanks", IntentKind.Rejection)]
    [InlineData("I need a plumber", IntentKind.ServiceRequest)]
    [InlineData("I offer carpentry", IntentKind.ProviderRegistration)]
    [InlineData("next", IntentKind.NextMatches)]
    [InlineData("increase range", IntentKind.IncreaseRange)]
    [InlineData("hello there", IntentKind.Greeting)]
    [InlineData("I do car mechanic?", IntentKind.ProviderRegistration)]
    public async Task DetectIntent_ShouldClassifyKeywords(string message, IntentKind expected)
    {
        var result = await _ai.DetectIntentAsync(message);

        result.Intent.ShouldBe(expected);
    }

    [Fact]
    public async Task ExtractServices_ShouldFindMultipleSlugs()
    {
        var result = await _ai.ExtractServicesAsync("I fix doors and repair laptops");

        result.Slugs.ShouldContain("carpentry");
        result.Slugs.ShouldContain("computer-repair");
    }

    [Theory]
    [InlineData("I do car Mechanic?", "auto-repair")]
    [InlineData("I offer auto repair", "auto-repair")]
    [InlineData("car repair available", "auto-repair")]
    [InlineData("I am a mechanic", "auto-repair")]
    public async Task ExtractServices_ShouldDetectMechanic(string text, string expected)
    {
        var result = await _ai.ExtractServicesAsync(text);

        result.Slugs.ShouldContain(expected);
    }

    [Fact]
    public async Task JudgeServiceMatch_ShouldFindContainmentMatch()
    {
        var result = await _ai.JudgeServiceMatchAsync("plumb", new[] { "plumbing", "carpentry" });

        result.MatchedSlug.ShouldBe("plumbing");
        result.IsNew.ShouldBeFalse();
    }

    [Fact]
    public async Task JudgeServiceMatch_ShouldReturnNewWhenNoCandidate()
    {
        var result = await _ai.JudgeServiceMatchAsync("astrology", new[] { "plumbing", "carpentry" });

        result.IsNew.ShouldBeTrue();
        result.ProposedSlug.ShouldBe("astrology");
    }

    [Fact]
    public async Task GenerateReply_ShouldEchoPurpose()
    {
        var ctx = new ReplyContext(
            Purpose: "confirm-services",
            RecentTurns: new[] { new ConversationTurn(TurnRole.User, "I do plumbing") });

        var reply = await _ai.GenerateReplyAsync(ctx);

        reply.ShouldContain("confirm-services");
        reply.ShouldContain("I do plumbing");
    }

    [Fact]
    public async Task DetectLanguage_ShouldRecognizeArabicScript()
    {
        var result = await _ai.DetectLanguageAsync("أحتاج سباك");

        result.LanguageCode.ShouldBe("ar");
    }
}
