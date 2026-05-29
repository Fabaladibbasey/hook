using Hook.Features.Ai;
using Shouldly;

namespace Hook.UnitTests.Ai;

public class QuickIntentPlatformQuestionTests
{
    [Theory]
    [InlineData("what is hook?")]
    [InlineData("how does this work")]
    [InlineData("Why do you need my number?")]
    [InlineData("can I cancel?")]
    [InlineData("Does this cost money?")]
    [InlineData("should I provide an address?")]
    public void LooksLikePlatformQuestion_GenuineQuestion_True(string text)
    {
        QuickIntent.LooksLikePlatformQuestion(text).ShouldBeTrue();
    }

    [Theory]
    // Service-request hint wins — "is my pipe leaking?" reads as ServiceRequest
    // via the possessive-problem pattern; the LLM is not the right defence here.
    [InlineData("is my pipe leaking?")]
    [InlineData("my fridge stopped working — can someone help?")]
    [InlineData("I need a plumber")]
    [InlineData("I offer carpentry")]
    public void LooksLikePlatformQuestion_StrongHint_False(string text)
    {
        QuickIntent.LooksLikePlatformQuestion(text).ShouldBeFalse();
    }

    [Theory]
    [InlineData("hi")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void LooksLikePlatformQuestion_TooShortOrEmpty_False(string? text)
    {
        QuickIntent.LooksLikePlatformQuestion(text).ShouldBeFalse();
    }

    [Theory]
    // Leads removed from the regex — these no longer match without trailing '?'.
    [InlineData("are you a bot")]
    [InlineData("will it cost something")]
    [InlineData("who can help me")]
    [InlineData("is the weather nice today")]
    public void LooksLikePlatformQuestion_DroppedLeadsWithoutTrailingQuestionMark_False(string text)
    {
        QuickIntent.LooksLikePlatformQuestion(text).ShouldBeFalse();
    }
}
