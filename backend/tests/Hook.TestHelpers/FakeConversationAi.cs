using System.Text.RegularExpressions;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;

namespace Hook.TestHelpers;

public sealed class FakeConversationAi : IConversationAi
{
    private readonly Dictionary<string, IntentDetectionResult> _intentOverrides = new(StringComparer.OrdinalIgnoreCase);

    public int DetectIntentCalls { get; private set; }
    public int ExtractEtaCalls { get; private set; }

    /// <summary>Force a specific (intent, confidence) for a given exact input — useful
    /// for integration tests that need to drive low-confidence disambiguation paths.</summary>
    public void OverrideIntent(string userMessage, IntentDetectionResult result) =>
        _intentOverrides[userMessage] = result;

    public void ResetOverrides()
    {
        _intentOverrides.Clear();
        _etaOverrides.Clear();
        DetectIntentCalls = 0;
        ExtractEtaCalls = 0;
    }

    public Task<IntentDetectionResult> DetectIntentAsync(string userMessage, CancellationToken ct = default)
    {
        DetectIntentCalls++;
        if (_intentOverrides.TryGetValue(userMessage, out var overridden))
            return Task.FromResult(overridden);

        var lower = Normalize(userMessage);
        var intent = lower switch
        {
            _ when Match(lower, "yes", "ok", "okay", "confirm", "yeah") => IntentKind.Confirmation,
            _ when Match(lower, "no", "cancel", "stop") => IntentKind.Rejection,
            _ when Match(lower, "edit", "change", "remove", "add") => IntentKind.Edit,
            _ when Match(lower, "next", "more", "not these") => IntentKind.NextMatches,
            _ when Match(lower, "increase", "wider", "expand") => IntentKind.IncreaseRange,
            _ when Match(lower,
                "contact details", "contact info", "chat link",
                "share contact", "share contacts", "share phone", "share number", "share link",
                "share the chat", "share the contact", "share the phone", "share the number",
                "send contact", "send phone", "send number", "send details",
                "give me their", "give me the contact", "give me the phone", "give me the contacts",
                "their phone", "their number", "their contact", "their details",
                "connect us", "connect me", "put us in touch", "put me in touch",
                "intro me", "intro us") => IntentKind.ShareContact,
            _ when Match(lower, "i need", "looking for", "find me", "i want") => IntentKind.ServiceRequest,
            _ when Match(lower, "i offer", "i do", "i fix", "i provide", "i can do", "available for") => IntentKind.ProviderRegistration,
            _ when Match(lower, "hi", "hello", "hey") => IntentKind.Greeting,
            _ => IntentKind.Unknown
        };

        return Task.FromResult(new IntentDetectionResult(intent, 0.85, "en", "fake stub"));
    }

    // Conservative typo map mirroring the spelling-normalization cue in the
    // ServiceExtractionSystem prompt — keeps the FakeConversationAi shape
    // close enough to the real extractor for integration tests to exercise
    // the same open-domain path.
    private static readonly Dictionary<string, string> SpellingFixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["analysist"] = "analyst",
        ["electritian"] = "electrician",
        ["carpinter"] = "carpenter",
        ["mecanic"] = "mechanic",
        ["plummer"] = "plumber",
    };

    public Task<ServiceExtractionResult> ExtractServicesAsync(string userMessage, CancellationToken ct = default)
    {
        var lower = NormalizeSpelling(Normalize(userMessage));
        var slugs = new List<string>();
        if (lower.Contains("plumb")) slugs.Add("plumbing");
        if (lower.Contains("carpent") || lower.Contains("door") || lower.Contains("wood")) slugs.Add("carpentry");
        if (lower.Contains("computer") || lower.Contains("laptop") || lower.Contains("pc")) slugs.Add("computer-repair");
        if (lower.Contains("delivery")) slugs.Add("delivery");
        if (lower.Contains("taxi") || lower.Contains("cab") || lower.Contains("passenger") || Regex.IsMatch(lower, @"\bride\b")) slugs.Add("ride");
        if (lower.Contains("paint")) slugs.Add("painting");
        if (lower.Contains("electric")) slugs.Add("electrical");
        if (lower.Contains("mechanic") || lower.Contains("auto") || lower.Contains("car repair")) slugs.Add("auto-repair");

        // Open-domain fallback: when canonical matchers produced nothing, look for a
        // provider-framing prefix and emit a kebab-case slug from the trailing phrase.
        // Mirrors the prompt's two-tier extraction so tests exercise the same path
        // listed providers take when adding a non-canonical service.
        if (slugs.Count == 0)
        {
            var m = Regex.Match(lower,
                @"\b(?:i\s+do|i\s+am(?:\s+an?)?|i'?m(?:\s+an?)?|i\s+offer|i\s+provide)\s+(?<phrase>[a-z][a-z\s]{2,})");
            if (m.Success)
            {
                var phrase = m.Groups["phrase"].Value.Trim();
                var slug = Slugify(phrase);
                if (slug.Length > 0) slugs.Add(slug);
            }
        }

        return Task.FromResult(new ServiceExtractionResult([.. slugs.Distinct()]));
    }

    private static string NormalizeSpelling(string text) =>
        Regex.Replace(text, @"\b[a-z]+\b", m =>
            SpellingFixes.TryGetValue(m.Value, out var fix) ? fix : m.Value);

    private static string Slugify(string phrase) =>
        Regex.Replace(phrase.Trim(), @"\s+", "-");

    public Task<ServiceJudgeResult> JudgeServiceMatchAsync(
        string proposedSlug,
        IReadOnlyList<string> candidateSlugs,
        CancellationToken ct = default)
    {
        var match = candidateSlugs.FirstOrDefault(c =>
            string.Equals(c, proposedSlug, StringComparison.OrdinalIgnoreCase) ||
            c.Contains(proposedSlug, StringComparison.OrdinalIgnoreCase) ||
            proposedSlug.Contains(c, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(match is not null
            ? new ServiceJudgeResult(match, false, string.Empty)
            : new ServiceJudgeResult(string.Empty, true, proposedSlug));
    }

    // Tests set entries here so the parent-inference handler reaches a deterministic
    // verdict without hitting Ollama. Empty map → every slug stays a root.
    public Dictionary<string, string> ParentMap { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cardiology"] = "doctor",
        [".net-developer"] = "software-engineering",
        ["dotnet-developer"] = "software-engineering",
        ["net-developer"] = "software-engineering",
        ["family-law"] = "lawyer",
        ["brake-repair"] = "mechanic",
        ["wedding-photography"] = "photographer",
    };

    public Task<string?> JudgeParentSlugAsync(
        string proposedSlug,
        IReadOnlyList<string> rootCandidates,
        IReadOnlyList<string> rawExamples,
        CancellationToken ct = default)
    {
        if (!ParentMap.TryGetValue(proposedSlug, out var parent)) return Task.FromResult<string?>(null);
        return Task.FromResult(rootCandidates.Contains(parent, StringComparer.Ordinal) ? parent : null);
    }

    public Task<string> GenerateReplyAsync(ReplyContext context, CancellationToken ct = default)
    {
        var lastUser = context.RecentTurns.LastOrDefault(t => t.Role == TurnRole.User);
        var preview = lastUser is null ? string.Empty : $" (re: \"{lastUser.Text}\")";
        return Task.FromResult($"[stub] {context.Purpose}{preview}");
    }

    public Task<LanguageDetectionResult> DetectLanguageAsync(string userMessage, CancellationToken ct = default)
    {
        var hasLatin = Regex.IsMatch(userMessage, @"[a-zA-Z]");
        var hasArabic = Regex.IsMatch(userMessage, @"[؀-ۿ]");
        var hasCjk = Regex.IsMatch(userMessage, @"[一-鿿]");

        var language = hasArabic ? "ar" : hasCjk ? "zh" : hasLatin ? "en" : "en";
        return Task.FromResult(new LanguageDetectionResult(language, 0.7));
    }

    private readonly Dictionary<string, DateTimeOffset?> _etaOverrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Override the ETA returned for a specific exact input so integration tests
    /// can drive the InProgress→ETA path without touching Ollama.</summary>
    public void OverrideEta(string userMessage, DateTimeOffset? eta) =>
        _etaOverrides[userMessage] = eta;

    public Task<DateTimeOffset?> ExtractEtaAsync(string userMessage, DateTimeOffset now, CancellationToken ct = default)
    {
        ExtractEtaCalls++;
        if (_etaOverrides.TryGetValue(userMessage, out var overridden))
            return Task.FromResult(overridden);

        var lower = Normalize(userMessage);

        // Heuristic only covers relative "in N (minutes|hours|days)" forms — that's
        // enough to drive the InProgress→ETA path without booting Ollama. Absolute
        // phrases ("tomorrow at 5pm", "Friday morning") are intentionally NOT
        // handled; tests that need those should call OverrideEta with the exact
        // user input + the desired DateTimeOffset.
        var rel = Regex.Match(lower, @"\bin\s+(?<n>\d+)\s+(?<u>min|mins|minute|minutes|hr|hrs|hour|hours|day|days)\b");
        if (rel.Success && int.TryParse(rel.Groups["n"].Value, out var n))
        {
            var unit = rel.Groups["u"].Value;
            var span = unit.StartsWith("min") ? TimeSpan.FromMinutes(n)
                : unit.StartsWith("hr") || unit.StartsWith("hour") ? TimeSpan.FromHours(n)
                : TimeSpan.FromDays(n);
            return Task.FromResult<DateTimeOffset?>(now + span);
        }

        return Task.FromResult<DateTimeOffset?>(null);
    }

    private static string Normalize(string text) =>
        text.ToLowerInvariant().TrimEnd('?', '!', '.', ' ');

    // Word-boundary match so short tokens like "ok"/"no"/"hi" do not collide with
    // substrings (e.g. "facebook" contains "ok", "snowboard" contains "no").
    private static bool Match(string text, params string[] needles) =>
        needles.Any(n => Regex.IsMatch(text, $@"\b{Regex.Escape(n)}\b"));
}
