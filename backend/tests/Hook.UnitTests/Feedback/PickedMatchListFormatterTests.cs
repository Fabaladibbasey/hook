using System.Globalization;
using Hook.Features.Feedback;
using Hook.Features.Matching.MatchAggregate;
using Shouldly;

namespace Hook.UnitTests.Feedback;

public class PickedMatchListFormatterTests
{
    private static Match BuildMatch(string phone, MatchKind kind, double distanceKm = 1.0) =>
        Match.Create(
            requestId: Guid.NewGuid(),
            providerPhone: phone,
            serviceSlug: "plumbing",
            distanceKm: distanceKm,
            score: 0.5,
            now: DateTimeOffset.UtcNow,
            kind: kind);

    [Fact]
    public void Format_OmitsTag_OnAllExact()
    {
        var matches = new[]
        {
            BuildMatch("+2204440001", MatchKind.Exact, 5.2),
            BuildMatch("+2204440002", MatchKind.Exact, 3.1),
        };

        var formatted = PickedMatchListFormatter.Format(matches);

        formatted.ShouldNotContain("(Related)");
        formatted.ShouldContain("1)");
        formatted.ShouldContain("2)");
        formatted.ShouldContain("5.2km away");
        formatted.ShouldContain("3.1km away");
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
        var parts = formatted.Split('\n');

        parts.Length.ShouldBe(3);
        parts[0].ShouldNotContain("(Related)");
        parts[1].ShouldContain("(Related)");
        parts[2].ShouldContain("(Related)");
    }

    [Fact]
    public void Format_RendersDistance_OnSinglePick()
    {
        var matches = new[] { BuildMatch("+2204440001", MatchKind.Exact, 2.7) };

        var formatted = PickedMatchListFormatter.Format(matches);

        formatted.ShouldBe("1) +220***01 — 2.7km away");
    }

    [Fact]
    public void Format_RendersDistanceAndRelatedTag_Together()
    {
        var matches = new[]
        {
            BuildMatch("+2204440001", MatchKind.Exact, 5.2),
            BuildMatch("+2204440002", MatchKind.Broadened, 3.1),
        };

        var formatted = PickedMatchListFormatter.Format(matches);

        formatted.ShouldBe("1) +220***01 — 5.2km away\n2) +220***02 — 3.1km away (Related)");
    }

    [Fact]
    public void Format_RoundsSubKilometer_ToOneDecimal()
    {
        var matches = new[] { BuildMatch("+2204440001", MatchKind.Exact, 0.34) };

        PickedMatchListFormatter.Format(matches).ShouldContain("0.3km away");
    }

    [Fact]
    public void Format_RendersZeroDistance_AsZeroPointZero()
    {
        var matches = new[] { BuildMatch("+2204440001", MatchKind.Exact, 0.0) };

        PickedMatchListFormatter.Format(matches).ShouldContain("0.0km away");
    }

    [Fact]
    public void Format_ReturnsEmpty_OnEmptyList()
    {
        PickedMatchListFormatter.Format(Array.Empty<Match>()).ShouldBe(string.Empty);
    }

    [Fact]
    public void Format_RendersDistance_InvariantCulture_RegardlessOfCurrentCulture()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var matches = new[] { BuildMatch("+2204440001", MatchKind.Exact, 5.2) };
            PickedMatchListFormatter.Format(matches).ShouldBe("1) +220***01 — 5.2km away");
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }
}
