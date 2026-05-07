using System.Text.RegularExpressions;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;

namespace Hook.TestHelpers;

public sealed class FakeConversationAi : IConversationAi
{
    private readonly Dictionary<string, IntentDetectionResult> _intentOverrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Force a specific (intent, confidence) for a given exact input — useful
    /// for integration tests that need to drive low-confidence disambiguation paths.</summary>
    public void OverrideIntent(string userMessage, IntentDetectionResult result) =>
        _intentOverrides[userMessage] = result;

    public void ResetOverrides() => _intentOverrides.Clear();

    public Task<IntentDetectionResult> DetectIntentAsync(string userMessage, CancellationToken ct = default)
    {
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

    public Task<ServiceExtractionResult> ExtractServicesAsync(string userMessage, CancellationToken ct = default)
    {
        var lower = Normalize(userMessage);
        var slugs = new List<string>();
        if (lower.Contains("plumb")) slugs.Add("plumbing");
        if (lower.Contains("carpent") || lower.Contains("door") || lower.Contains("wood")) slugs.Add("carpentry");
        if (lower.Contains("computer") || lower.Contains("laptop") || lower.Contains("pc")) slugs.Add("computer-repair");
        if (lower.Contains("delivery")) slugs.Add("delivery");
        if (lower.Contains("taxi") || lower.Contains("cab") || lower.Contains("passenger") || Regex.IsMatch(lower, @"\bride\b")) slugs.Add("ride");
        if (lower.Contains("paint")) slugs.Add("painting");
        if (lower.Contains("electric")) slugs.Add("electrical");
        if (lower.Contains("mechanic") || lower.Contains("auto") || lower.Contains("car repair")) slugs.Add("auto-repair");

        return Task.FromResult(new ServiceExtractionResult(slugs.Distinct().ToArray()));
    }

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
            ? new ServiceJudgeResult(match, false, null)
            : new ServiceJudgeResult(null, true, proposedSlug));
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

    private static string Normalize(string text) =>
        text.ToLowerInvariant().TrimEnd('?', '!', '.', ' ');

    // Word-boundary match so short tokens like "ok"/"no"/"hi" do not collide with
    // substrings (e.g. "facebook" contains "ok", "snowboard" contains "no").
    private static bool Match(string text, params string[] needles) =>
        needles.Any(n => Regex.IsMatch(text, $@"\b{Regex.Escape(n)}\b"));
}
