using Hook.Features.Feedback.AggregateStats;
using Hook.Features.Feedback.Models;

namespace Hook.UnitTests.Feedback;

public class FeedbackResponseServiceTests
{
    [Theory]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData("Y")]
    [InlineData("  yes  ")]
    public void ParseAnswer_YesVariants_ReturnsYes(string input) =>
        Assert.Equal(FeedbackAnswer.Yes, FeedbackResponseService.ParseAnswer(input));

    [Theory]
    [InlineData("no")]
    [InlineData("N")]
    [InlineData("NO")]
    public void ParseAnswer_NoVariants_ReturnsNo(string input) =>
        Assert.Equal(FeedbackAnswer.No, FeedbackResponseService.ParseAnswer(input));

    [Theory]
    [InlineData("in progress")]
    [InlineData("IN PROGRESS")]
    [InlineData("yes, in progress actually")]
    [InlineData("the work is in progress")]
    public void ParseAnswer_InProgress_ReturnsInProgress(string input) =>
        Assert.Equal(FeedbackAnswer.InProgress, FeedbackResponseService.ParseAnswer(input));

    [Theory]
    [InlineData("not in progress")]
    [InlineData("NOT IN PROGRESS")]
    [InlineData("the job is not in progress at all")]
    public void ParseAnswer_NotInProgress_ReturnsNo(string input) =>
        Assert.Equal(FeedbackAnswer.No, FeedbackResponseService.ParseAnswer(input));

    [Theory]
    [InlineData("")]
    [InlineData("maybe")]
    [InlineData("idk")]
    public void ParseAnswer_Unrecognised_ReturnsNull(string input) =>
        Assert.Null(FeedbackResponseService.ParseAnswer(input));

    [Fact]
    public void ParseAnswer_Whitespace_ReturnsNull() =>
        Assert.Null(FeedbackResponseService.ParseAnswer("   "));
}
