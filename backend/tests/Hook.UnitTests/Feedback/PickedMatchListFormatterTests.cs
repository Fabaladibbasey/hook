using Hook.Features.Feedback;
using Hook.Features.Matching.MatchAggregate;
using Shouldly;

namespace Hook.UnitTests.Feedback;

public class PickedMatchListFormatterTests
{
    private static Match BuildMatch(string phone, MatchKind kind) =>
        Match.Create(
            requestId: Guid.NewGuid(),
            providerPhone: phone,
            serviceSlug: "plumbing",
            distanceKm: 1.0,
            score: 0.5,
            now: DateTimeOffset.UtcNow,
            kind: kind);

    [Fact]
    public void Format_OmitsTag_OnAllExact()
    {
        var matches = new[]
        {
            BuildMatch("+2204440001", MatchKind.Exact),
            BuildMatch("+2204440002", MatchKind.Exact),
        };

        var formatted = PickedMatchListFormatter.Format(matches);

        formatted.ShouldNotContain("(Related)");
        formatted.ShouldContain("1)");
        formatted.ShouldContain("2)");
    }

    [Fact]
    public void Format_AppendsRelatedTag_OnlyToNonExactRows()
    {
        var matches = new[]
        {
            BuildMatch("+2204440001", MatchKind.Exact),
            BuildMatch("+2204440002", MatchKind.Broadened),
            BuildMatch("+2204440003", MatchKind.Narrowed),
        };

        var formatted = PickedMatchListFormatter.Format(matches);
        var parts = formatted.Split(", ");

        parts.Length.ShouldBe(3);
        parts[0].ShouldNotContain("(Related)");
        parts[1].ShouldContain("(Related)");
        parts[2].ShouldContain("(Related)");
    }

    [Fact]
    public void Format_ReturnsEmpty_OnEmptyList()
    {
        PickedMatchListFormatter.Format(Array.Empty<Match>()).ShouldBe(string.Empty);
    }
}
