using Hook.Features.ContactSharing.ExchangePhones;

namespace Hook.UnitTests.ContactSharing;

public class PickRegexTests
{
    [Theory]
    [InlineData("ok 1 sec")]
    [InlineData("give me 2 mins")]
    [InlineData("got 3 quotes already")]
    [InlineData("call you in 5")]
    [InlineData("I'll be there in 10")]
    [InlineData("best of all worlds")]
    [InlineData("not at all")]
    [InlineData("got 1 and 2 quotes already")]
    [InlineData("1 and done")]
    [InlineData("call me in 1 or 2 mins")]
    public void IsPickIntent_ConversationalChatter_ReturnsFalse(string text)
    {
        Assert.False(PickProviderResolver.IsPickIntent(text),
            $"'{text}' is conversational, not a pick command");
    }

    [Theory]
    [InlineData("PICK 1")]
    [InlineData("pick 1,2")]
    [InlineData("PICK ALL")]
    [InlineData("pick all")]
    [InlineData("PICK 99")]
    [InlineData("PICK 100")]   // gate accepts via \bpick\b; resolver drops as out-of-range
    [InlineData("PICK 0")]
    [InlineData("PICK -1")]
    [InlineData("#1")]
    [InlineData("PICK 1.")]
    [InlineData("pick 1 and 2")]
    [InlineData("PICK 1 & 2")]
    [InlineData("pick 1+2")]
    [InlineData("pick 1 or 2")]
    public void IsPickIntent_ExplicitPick_ReturnsTrue(string text)
    {
        Assert.True(PickProviderResolver.IsPickIntent(text),
            $"'{text}' contains the explicit 'pick' keyword and must pass the gate");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("ALL")]
    [InlineData("all")]
    [InlineData("1,2")]
    [InlineData(" 2 ")]
    [InlineData("1 and 2")]
    [InlineData("1 & 2")]
    [InlineData("1+2")]
    [InlineData("1 or 2")]
    [InlineData("1, 2 and 3")]
    [InlineData("1 and 2, 3")]
    [InlineData("1, 2, 3 and 4")]
    [InlineData("1 + 2 & 3")]
    public void IsPickIntent_StandalonePickSyntax_ReturnsTrue(string text)
    {
        Assert.True(PickProviderResolver.IsPickIntent(text),
            $"'{text}' is a standalone pick token");
    }

    [Theory]
    [InlineData("100")]            // exceeds [1-9]\d? cap on bare-index path
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("   ")]            // whitespace only
    [InlineData("#1 thanks")]      // trailing words break whole-message anchor
    [InlineData("1, 2.")]          // trailing punctuation breaks whole-message anchor
    public void IsPickIntent_OutOfRangeOrPunctuated_ReturnsFalse(string text)
    {
        Assert.False(PickProviderResolver.IsPickIntent(text),
            $"'{text}' must not pass the gate as bare-index syntax");
    }
}
