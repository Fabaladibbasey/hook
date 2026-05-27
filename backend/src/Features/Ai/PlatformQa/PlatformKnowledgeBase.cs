using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Hook.Features.Ai.PlatformQa;

// Embedded markdown KB used as the grounding payload for AnswerPlatformQuestionAsync.
// Files live under Features/Ai/KnowledgeBase/*.md and ship as <EmbeddedResource> so they
// survive `dotnet publish /t:PublishContainer` without depending on content-layer placement
// and cannot be tampered post-deploy.
public sealed class PlatformKnowledgeBase
{
    private readonly Lazy<string> _content;

    public PlatformKnowledgeBase(IOptions<PlatformKnowledgeBaseOptions> options)
    {
        var max = options.Value.MaxKbChars;
        _content = new Lazy<string>(() => Load(max), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Content => _content.Value;

    private static string Load(int maxChars)
    {
        var asm = typeof(PlatformKnowledgeBase).Assembly;
        var prefix = $"{asm.GetName().Name}.Features.Ai.KnowledgeBase.";

        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                        && n.EndsWith(".md", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        if (names.Length == 0)
            throw new InvalidOperationException(
                "PlatformKnowledgeBase: no embedded *.md resources found. "
              + "Verify <EmbeddedResource> entries in Hook.csproj.");

        var content = string.Join("\n\n", names.Select(name =>
        {
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing embedded resource {name}");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return StripMarkdownHeaders(reader.ReadToEnd().TrimEnd());
        }));

        // OllamaConversationAi.AnswerPlatformQuestionAsync wraps this content in
        // <knowledge_base>...</knowledge_base> fences. A KB file containing the
        // literal tag would break the fence and let prompt-injected content
        // escape into the system-prompt role. Fail fast at load.
        if (content.Contains("</knowledge_base>", StringComparison.OrdinalIgnoreCase)
            || content.Contains("<knowledge_base>", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "PlatformKnowledgeBase: KB source contains a <knowledge_base> tag; "
              + "this would break the OllamaConversationAi prompt fence. Rename it.");
        }

        if (content.Length <= maxChars) return content;

        // Surrogate-pair safe truncation: if the cap lands on a high surrogate,
        // back up one char so the resulting string is well-formed UTF-16.
        var cut = maxChars;
        if (char.IsHighSurrogate(content[cut - 1])) cut--;
        return content[..cut];
    }

    // Drop every CommonMark ATX header line. Source .md files keep their
    // headings for developer readability; the LLM only sees prose, removing the
    // dominant parroting risk (verbatim "# What Hook is" or "## Steps" lines
    // showing up in user-facing replies). The PlatformAnswerSystem prompt also
    // bans header echo as defense in depth.
    //
    // Strict regex: `^\s*#{1,6}\s` — preserves `#hashtag`, `#1. numbered`,
    // and `####### too-many-hashes` (none of which are ATX headers).
    private static readonly Regex AtxHeaderRx = new(
        @"^\s*#{1,6}\s", RegexOptions.Compiled);

    private static string StripMarkdownHeaders(string body) =>
        string.Join('\n',
            body.Split('\n').Where(line => !AtxHeaderRx.IsMatch(line)));

    internal static string StripMarkdownHeadersForTest(string body) => StripMarkdownHeaders(body);
}
