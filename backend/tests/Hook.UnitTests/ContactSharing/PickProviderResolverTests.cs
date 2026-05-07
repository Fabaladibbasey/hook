using Hook.Features.ContactSharing.ExchangePhones;
using Hook.Features.Matching.MatchAggregate;

namespace Hook.UnitTests.ContactSharing;

public class PickProviderResolverTests
{
    private static IReadOnlyList<Match> Five() => Enumerable.Range(0, 5)
        .Select(i => new Match
        {
            RequestId = Guid.NewGuid(),
            ProviderPhone = $"+220300000{i}",
            ServiceSlug = "plumbing"
        }).ToList();

    [Fact]
    public void Resolve_SingleIndex_ReturnsOneMatch()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("PICK 1", matches);
        Assert.Single(picked);
        Assert.Equal(matches[0].Id, picked[0].Id);
    }

    [Fact]
    public void Resolve_HashSingleIndex_ReturnsOneMatch()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("#1", matches);
        Assert.Single(picked);
        Assert.Equal(matches[0].Id, picked[0].Id);
    }

    [Fact]
    public void Resolve_BareDigit_ReturnsOneMatch()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("1", matches);
        Assert.Single(picked);
        Assert.Equal(matches[0].Id, picked[0].Id);
    }

    [Fact]
    public void Resolve_CommaSeparated_ReturnsMultipleDistinctMatchesInOrder()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("PICK 1,3,5", matches);
        Assert.Equal(new[] { matches[0].Id, matches[2].Id, matches[4].Id }, picked.Select(m => m.Id));
    }

    [Fact]
    public void Resolve_All_ReturnsEveryPresentedMatch()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("PICK ALL", matches);
        Assert.Equal(5, picked.Count);
    }

    [Fact]
    public void Resolve_AllLowercase_StillReturnsAll()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("pick all", matches);
        Assert.Equal(5, picked.Count);
    }

    [Fact]
    public void Resolve_OutOfRangeIndex_IsSkippedNotFailed()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("PICK 1,99", matches);
        Assert.Single(picked);
        Assert.Equal(matches[0].Id, picked[0].Id);
    }

    [Fact]
    public void Resolve_DuplicateIndices_AreDeduplicatedPreservingOrder()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("PICK 2,2,3", matches);
        Assert.Equal(new[] { matches[1].Id, matches[2].Id }, picked.Select(m => m.Id));
    }

    [Fact]
    public void Resolve_FullPhone_AlwaysWinsRegardlessOfPickKeyword()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("call +2203000003", matches);
        Assert.Single(picked);
        Assert.Equal(matches[3].Id, picked[0].Id);
    }

    [Fact]
    public void Resolve_FullPhoneAmongDigits_StillMatches()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("call me at +2203000003 plumber", matches);
        Assert.Single(picked);
        Assert.Equal(matches[3].Id, picked[0].Id);
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
        var picked = PickProviderResolver.Resolve("PICK ALL", Array.Empty<Match>());
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
    public void Resolve_PhoneFragmentSingleHit_WithPickKeyword_Picks()
    {
        var matches = new[]
        {
            new Match { RequestId = Guid.NewGuid(), ProviderPhone = "+2203331234", ServiceSlug = "plumbing" },
            new Match { RequestId = Guid.NewGuid(), ProviderPhone = "+2207775678", ServiceSlug = "plumbing" },
        };
        var picked = PickProviderResolver.Resolve("pick 1234", matches);
        Assert.Single(picked);
        Assert.Equal(matches[0].Id, picked[0].Id);
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
