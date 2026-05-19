using Hook.Features.Ai;

namespace Hook.UnitTests.Ai;

public class FuzzyMatchTests
{
    [Theory]
    [InlineData("yes", new[] { "yes" }, 1, true)]
    [InlineData("yse", new[] { "yes" }, 1, true)]
    [InlineData("yeh", new[] { "yeah" }, 1, true)]
    [InlineData("noo", new[] { "no" }, 1, true)]
    [InlineData("yes", new[] { "no" }, 1, false)]
    [InlineData("", new[] { "yes" }, 1, false)]
    [InlineData("yes", new string[] { }, 1, false)]
    [InlineData("byee", new[] { "bye" }, 1, true)]
    [InlineData("byyye", new[] { "bye" }, 1, false)] // distance 2
    [InlineData("aab", new[] { "aba" }, 1, true)]            // transposition with repeated letter
    [InlineData("YES", new[] { "yes" }, 0, true)]            // case-insensitive exact match
    [InlineData("Yse", new[] { "yes" }, 1, true)]            // case-insensitive transposition
    [InlineData("yes", new[] { "yes" }, 0, true)]            // exact-match-only mode
    [InlineData("yse", new[] { "yes" }, 0, false)]           // exact-match-only rejects typos
    public void MatchesAny_ReturnsExpected(string input, string?[] tokens, int max, bool expected)
        => Assert.Equal(expected, FuzzyMatch.MatchesAny(input, tokens, max));

    [Fact]
    public void MatchesAny_NullTokenInList_IsSkipped()
        => Assert.True(FuzzyMatch.MatchesAny("yes", [null, "yes"], 1));
}
