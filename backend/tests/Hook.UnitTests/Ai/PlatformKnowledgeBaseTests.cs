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
        // Headers are stripped on load (parroting defense). Assert on body prose
        // unique to each section so the test still catches an accidentally-missing
        // section file.
        content.ShouldContain("WhatsApp-first marketplace");          // overview
        content.ShouldContain("end-to-end encrypted");                // privacy
        content.ShouldContain("daily background sweep");              // retention
        content.ShouldContain("Pick a provider");                     // request flow
        content.ShouldContain("WhatsApp location pin");               // register flow
        content.ShouldContain("Any reply from your number extends the timer"); // register heartbeat
        content.ShouldContain("free for both clients and providers"); // common questions
    }

    [Fact]
    public void Content_DropsAllMarkdownHeaders()
    {
        var content = Build().Content;
        content.ShouldNotContain("# What Hook is");
        content.ShouldNotContain("## Steps");
        content.ShouldNotContain("## Cost");
        // Defense in depth: every '#'-prefixed line (top- and sub-headers) must go.
        foreach (var line in content.Split('\n'))
            line.TrimStart().StartsWith('#').ShouldBeFalse(
                $"header line leaked into KB content: '{line}'");
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

    [Fact]
    public void Load_KbContent_DoesNotContainKnowledgeBaseFenceTag()
    {
        // OllamaConversationAi wraps Content in <knowledge_base>...</knowledge_base>.
        // A KB file containing either tag literally would break the fence and
        // let prompt-injected content escape into the system role. Load() asserts
        // this; the sentinel here documents the contract for KB editors.
        var content = Build().Content;
        content.ShouldNotContain("</knowledge_base>", Case.Insensitive);
        content.ShouldNotContain("<knowledge_base>", Case.Insensitive);
    }

    [Theory]
    [InlineData("# Real Header\nprose", "prose")]
    [InlineData("## Sub\nprose", "prose")]
    [InlineData("###### Six\nprose", "prose")]
    [InlineData("   # Indented Header\nprose", "prose")]
    public void StripMarkdownHeaders_DropsAtxHeaders(string input, string expected) =>
        PlatformKnowledgeBase.StripMarkdownHeadersForTest(input).ShouldBe(expected);

    [Theory]
    [InlineData("#hashtag prose")]
    [InlineData("#1. numbered")]
    [InlineData("####### too many hashes")]
    public void StripMarkdownHeaders_PreservesNonAtx(string input) =>
        PlatformKnowledgeBase.StripMarkdownHeadersForTest(input).ShouldBe(input);
}
