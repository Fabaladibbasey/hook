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
    public void Resolve_NoIndex_FallsBackToPhoneFragment()
    {
        var matches = Five();
        var picked = PickProviderResolver.Resolve("call +2203000003", matches);
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
}
