using Hook.Features.ContactSharing.ExchangePhones;
using Hook.Features.Matching.MatchAggregate;

namespace Hook.UnitTests.ContactSharing;

public class PickProviderResolverTests
{
    private static IReadOnlyList<Match> Five() => [.. Enumerable.Range(0, 5)
        .Select(i => new Match
        {
            RequestId = Guid.NewGuid(),
            ProviderPhone = $"+220300000{i}",
            ServiceSlug = "plumbing"
        })];

    [Fact]
    public void Resolve_SingleIndex_ReturnsOneMatchAtPosition1()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("PICK 1", matches);
        var single = Assert.Single(picked);
        Assert.Equal(matches[0].Id, single.Match.Id);
        Assert.Equal(1, single.Position);
    }

    [Fact]
    public void Resolve_HashSingleIndex_ReturnsOneMatchAtPosition1()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("#1", matches);
        var single = Assert.Single(picked);
        Assert.Equal(matches[0].Id, single.Match.Id);
        Assert.Equal(1, single.Position);
    }

    [Fact]
    public void Resolve_BareDigit_ReturnsOneMatchAtPosition1()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("1", matches);
        var single = Assert.Single(picked);
        Assert.Equal(matches[0].Id, single.Match.Id);
        Assert.Equal(1, single.Position);
    }

    [Fact]
    public void Resolve_CommaSeparated_ReturnsMultipleDistinctMatchesInOrder_WithPositions()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("PICK 1,3,5", matches);
        Assert.Equal(new[] { matches[0].Id, matches[2].Id, matches[4].Id }, picked.Select(p => p.Match.Id));
        Assert.Equal(new[] { 1, 3, 5 }, picked.Select(p => p.Position));
    }

    [Fact]
    public void Resolve_All_ReturnsEveryPresentedMatch_WithIncreasingPositions()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("PICK ALL", matches);
        Assert.Equal(5, picked.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, picked.Select(p => p.Position));
    }

    [Fact]
    public void Resolve_AllLowercase_StillReturnsAll()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("pick all", matches);
        Assert.Equal(5, picked.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, picked.Select(p => p.Position));
    }

    [Fact]
    public void Resolve_OutOfRangeIndex_IsSkippedNotFailed()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("PICK 1,99", matches);
        var single = Assert.Single(picked);
        Assert.Equal(matches[0].Id, single.Match.Id);
        Assert.Equal(1, single.Position);
    }

    [Fact]
    public void Resolve_DuplicateIndices_AreDeduplicatedPreservingOrder()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("PICK 2,2,3", matches);
        Assert.Equal(new[] { matches[1].Id, matches[2].Id }, picked.Select(p => p.Match.Id));
        Assert.Equal(new[] { 2, 3 }, picked.Select(p => p.Position));
    }

    [Fact]
    public void Resolve_FullPhone_AlwaysWinsRegardlessOfPickKeyword_PositionFromIndex()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("call +2203000003", matches);
        var single = Assert.Single(picked);
        Assert.Equal(matches[3].Id, single.Match.Id);
        Assert.Equal(4, single.Position);
    }

    [Fact]
    public void Resolve_FullPhoneAmongDigits_StillMatches_PositionFromIndex()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("call me at +2203000003 plumber", matches);
        var single = Assert.Single(picked);
        Assert.Equal(matches[3].Id, single.Match.Id);
        Assert.Equal(4, single.Position);
    }

    [Fact]
    public void Resolve_NoMatchAtAll_ReturnsEmpty()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("nope", matches);
        Assert.Empty(picked);
    }

    [Fact]
    public void Resolve_EmptyMatchList_ReturnsEmpty()
    {
        var picked = PickProviderResolver.Resolve("PICK ALL", []);
        Assert.Empty(picked);
    }

    [Fact]
    public void Resolve_PhoneFragmentCollision_TwoProviders_ReturnsEmpty()
    {
        var matches = new[]
        {
            new Match { RequestId = Guid.NewGuid(), ProviderPhone = "+2203331234", ServiceSlug = "plumbing" },
            new Match { RequestId = Guid.NewGuid(), ProviderPhone = "+2207771234", ServiceSlug = "plumbing" },
        };
        var picked = PickProviderResolver.Resolve("pick 1234", matches);
        Assert.Empty(picked);
    }

    [Fact]
    public void Resolve_PhoneFragmentCollision_ThreeProviders_ReturnsEmpty()
    {
        var matches = new[]
        {
            new Match { RequestId = Guid.NewGuid(), ProviderPhone = "+2203331234", ServiceSlug = "plumbing" },
            new Match { RequestId = Guid.NewGuid(), ProviderPhone = "+2207771234", ServiceSlug = "plumbing" },
            new Match { RequestId = Guid.NewGuid(), ProviderPhone = "+2204441234", ServiceSlug = "plumbing" },
        };
        var picked = PickProviderResolver.Resolve("pick 1234", matches);
        Assert.Empty(picked);
    }

    [Fact]
    public void Resolve_PhoneFragmentSingleHit_WithPickKeyword_Picks_PositionFromIndex()
    {
        var matches = new[]
        {
            new Match { RequestId = Guid.NewGuid(), ProviderPhone = "+2207775678", ServiceSlug = "plumbing" },
            new Match { RequestId = Guid.NewGuid(), ProviderPhone = "+2203331234", ServiceSlug = "plumbing" },
        };
        var picked = PickProviderResolver.Resolve("pick 1234", matches);
        var single = Assert.Single(picked);
        Assert.Equal(matches[1].Id, single.Match.Id);
        Assert.Equal(2, single.Position);
    }

    [Fact]
    public void Resolve_PhoneFragmentSingleHit_NoPickKeyword_ReturnsEmpty()
    {
        // "1234 main st" has no `pick` keyword, so the last-4 fragment fallback is
        // skipped and the resolver returns empty — preventing accidental picks from
        // conversational digits.
        var matches = new[]
        {
            new Match { RequestId = Guid.NewGuid(), ProviderPhone = "+2203331234", ServiceSlug = "plumbing" },
            new Match { RequestId = Guid.NewGuid(), ProviderPhone = "+2207775678", ServiceSlug = "plumbing" },
        };
        var picked = PickProviderResolver.Resolve("1234 main st", matches);
        Assert.Empty(picked);
    }

    [Fact]
    public void Resolve_PhoneFragmentDigitBounded_NotMatchedInsideLongerDigits()
    {
        // "pick 12340" should NOT fragment-match the provider ending in "1234" because
        // "1234" appears inside the digit run "12340" — the bounded match requires the
        // edges to be non-digit (or string boundary).
        var matches = new[]
        {
            new Match { RequestId = Guid.NewGuid(), ProviderPhone = "+2203331234", ServiceSlug = "plumbing" },
        };
        var picked = PickProviderResolver.Resolve("pick 12340", matches);
        Assert.Empty(picked);
    }

    [Fact]
    public void Resolve_Pick100_NoMatchesOfThatSize_ReturnsEmpty()
    {
        // Gate accepts via \bpick\b. Parser truncates "100" to "10" via [1-9]\d?,
        // tries index 10 against 5 matches → out of range → indices empty. No full
        // phone, no last-4 fragment match → resolver returns empty.
        var matches = Five();
        var picked = PickProviderResolver.Resolve("PICK 100", matches);
        Assert.Empty(picked);
    }
}
