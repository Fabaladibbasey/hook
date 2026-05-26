using Hook.Features.Ai.PlatformQa;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Hook.UnitTests.Ai;

public class PlatformKnowledgeBaseTests
{
    private static PlatformKnowledgeBase Build(int max = 16000) =>
        new(Options.Create(new PlatformKnowledgeBaseOptions { MaxKbChars = max }));

    [Fact]
    public void Content_ShipsAllSixSections()
    {
        var content = Build().Content;
        content.ShouldNotBeNullOrWhiteSpace();
        // Every embedded section file leads with a top-level heading; cheaper than
        // asserting file count and keeps the test resilient to KB edits that don't
        // change section topology.
        content.ShouldContain("# What Hook is");
        content.ShouldContain("# Privacy and encryption");
        content.ShouldContain("# Data retention");
        content.ShouldContain("# Requesting a service");
        content.ShouldContain("# Registering as a provider");
        content.ShouldContain("# Common questions");
    }

    [Fact]
    public void Content_IsIdempotent()
    {
        var kb = Build();
        var first = kb.Content;
        var second = kb.Content;
        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Fact]
    public void Content_HonorsMaxKbCharsCap()
    {
        var truncated = Build(max: 1200).Content;
        truncated.Length.ShouldBeLessThanOrEqualTo(1200);
    }
}
