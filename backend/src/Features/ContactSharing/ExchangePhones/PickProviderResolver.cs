using System.Text.RegularExpressions;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;

namespace Hook.Features.ContactSharing.ExchangePhones;

public sealed record PickedMatch(MatchEntity Match, int Position);

public static class PickProviderResolver
{
    private static readonly Regex IndexListRegex = new(
        @"([1-9]\d?(?:\s*,\s*[1-9]\d?)*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AllRegex = new(@"\ball\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PickKeywordRegex = new(@"\bpick\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Whole-message pick syntax: "1", "ALL", "1,2", "#1", " 2 ". Mirrors the digit
    // cap from IndexListRegex ([1-9]\d?) so out-of-range tokens like "100" don't
    // pass the gate via the bare-index branch — only via the explicit \bpick\b path.
    private static readonly Regex BareIndexSyntaxRegex = new(
        @"^\s*(?:all|#?[1-9]\d?(?:\s*,\s*#?[1-9]\d?)*)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Gate for InboundRouter. Returns true when the message looks like a pick command
    /// (explicit "pick" anywhere or a whole-message bare-index syntax). Conversational
    /// digits like "ok 1 sec" or "best of all worlds" do NOT match.
    /// </summary>
    public static bool IsPickIntent(string text) =>
        PickKeywordRegex.IsMatch(text) || BareIndexSyntaxRegex.IsMatch(text);

    public static IReadOnlyList<PickedMatch> Resolve(string text, IReadOnlyList<MatchEntity> matches)
    {
        if (matches.Count == 0) return Array.Empty<PickedMatch>();

        if (AllRegex.IsMatch(text))
            return [.. matches.Select((m, i) => new PickedMatch(m, i + 1))];

        var indices = ParseIndices(text, matches.Count);
        if (indices.Count > 0)
            return [.. indices.Select(i => new PickedMatch(matches[i], i + 1))];

        // Full-phone match wins over last-4 — if the user typed the full E.164 we
        // accept it as an unambiguous pick regardless of the pick keyword.
        var fullIdx = IndexOf(matches, m => text.Contains(m.ProviderPhone, StringComparison.Ordinal));
        if (fullIdx >= 0) return [new PickedMatch(matches[fullIdx], fullIdx + 1)];

        // Last-4 fragment fallback runs only when the user explicitly typed "pick".
        // Without the keyword, an incidental digit run ("1234 main st") could
        // exchange contact info without intent.
        if (!PickKeywordRegex.IsMatch(text)) return Array.Empty<PickedMatch>();

        var fragmentIdx = MatchByLastFourFragmentIndex(text, matches);
        return fragmentIdx < 0
            ? Array.Empty<PickedMatch>()
            : [new PickedMatch(matches[fragmentIdx], fragmentIdx + 1)];
    }

    private static int IndexOf(IReadOnlyList<MatchEntity> matches, Func<MatchEntity, bool> pred)
    {
        for (var i = 0; i < matches.Count; i++)
            if (pred(matches[i])) return i;
        return -1;
    }

    private static IReadOnlyList<int> ParseIndices(string text, int count)
    {
        var match = IndexListRegex.Match(text);
        if (!match.Success) return Array.Empty<int>();

        var seen = new HashSet<int>();
        var ordered = new List<int>();
        foreach (var token in match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, out var n)) continue;
            if (n < 1 || n > count) continue;
            var zeroBased = n - 1;
            if (seen.Add(zeroBased)) ordered.Add(zeroBased);
        }
        return ordered;
    }

    /// <summary>
    /// Match against the last 4 digits of any provider phone, requiring digit-bounded
    /// edges so "1234 main st" does not match a phone ending in 1234. On collision
    /// (two or more providers share the trailing 4) returns -1; the caller drops the
    /// inbound silently and the user can retry with an explicit index.
    /// </summary>
    private static int MatchByLastFourFragmentIndex(string text, IReadOnlyList<MatchEntity> matches)
    {
        var hit = -1;
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            if (m.ProviderPhone.Length < 4) continue;
            if (!HasDigitBoundedFragment(text, m.ProviderPhone[^4..])) continue;
            if (hit >= 0) return -1; // collision
            hit = i;
        }
        return hit;
    }

    private static bool HasDigitBoundedFragment(string text, string fragment)
    {
        var idx = text.IndexOf(fragment, StringComparison.Ordinal);
        while (idx >= 0)
        {
            var leftClear = idx == 0 || !char.IsDigit(text[idx - 1]);
            var rightIdx = idx + fragment.Length;
            var rightClear = rightIdx >= text.Length || !char.IsDigit(text[rightIdx]);
            if (leftClear && rightClear) return true;
            idx = text.IndexOf(fragment, idx + 1, StringComparison.Ordinal);
        }
        return false;
    }
}
