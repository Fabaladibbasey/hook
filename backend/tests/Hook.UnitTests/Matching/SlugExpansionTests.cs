using Hook.Features.Matching.Match;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Shouldly;

namespace Hook.UnitTests.Matching;

public class SlugExpansionTests
{
    [Fact]
    public void Classify_ReturnsExact_ForRequested()
    {
        var slugs = new ExpandedSlugs("plumbing", Parent: null, Children: ["pipe-repair"]);
        slugs.Classify(new HashSet<string> { "plumbing" }).ShouldBe(MatchKind.Exact);
    }

    [Fact]
    public void Classify_ReturnsNarrowed_WhenProviderHasChildOnly()
    {
        var slugs = new ExpandedSlugs("software-engineering", Parent: null,
            Children: ["net-developer"]);
        slugs.Classify(new HashSet<string> { "net-developer" }).ShouldBe(MatchKind.Narrowed);
    }

    [Fact]
    public void Classify_ReturnsBroadened_WhenProviderHasParentOnly()
    {
        var slugs = new ExpandedSlugs("cardiology", Parent: "doctor", Children: Array.Empty<string>());
        slugs.Classify(new HashSet<string> { "doctor" }).ShouldBe(MatchKind.Broadened);
    }

    [Fact]
    public void Classify_ReturnsExact_WhenProviderHasBothRequestedAndChild()
    {
        var slugs = new ExpandedSlugs("software-engineering", Parent: null,
            Children: ["net-developer"]);
        slugs.Classify(new HashSet<string> { "software-engineering", "net-developer" })
            .ShouldBe(MatchKind.Exact);
    }

    [Fact]
    public void All_IncludesRequested_OmitsNullParent()
    {
        var slugs = new ExpandedSlugs("plumbing", Parent: null, Children: ["pipe-repair"]);
        slugs.All.ShouldBe(["plumbing", "pipe-repair"]);
    }

    [Fact]
    public void All_IncludesParent_WhenPresent()
    {
        var slugs = new ExpandedSlugs("cardiology", Parent: "doctor", Children: ["pediatric-cardiology"]);
        slugs.All.ShouldBe(["cardiology", "doctor", "pediatric-cardiology"]);
    }

    [Fact]
    public void All_OmitsChildren_WhenEmpty()
    {
        var slugs = new ExpandedSlugs("cardiology", Parent: "doctor", Children: Array.Empty<string>());
        slugs.All.ShouldBe(["cardiology", "doctor"]);
    }

    [Fact]
    public void All_ContainsOnlyRequested_WhenParentNullAndChildrenEmpty()
    {
        var slugs = new ExpandedSlugs("plumbing", Parent: null, Children: Array.Empty<string>());
        slugs.All.ShouldBe(["plumbing"]);
    }

    [Fact]
    public void All_IsSameInstance_AcrossMultipleReads()
    {
        var slugs = new ExpandedSlugs("plumbing", Parent: null, Children: ["pipe-repair"]);
        ReferenceEquals(slugs.All, slugs.All).ShouldBeTrue();
    }
}
