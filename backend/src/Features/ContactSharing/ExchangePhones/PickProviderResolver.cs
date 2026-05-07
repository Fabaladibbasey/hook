using System.Text.RegularExpressions;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;

namespace Hook.Features.ContactSharing.ExchangePhones;

public static class PickProviderResolver
{
    public static readonly Regex PickRegex = new(
        @"\bpick\b|\ball\b|^\s*#?\s*\d",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IndexListRegex = new(
        @"([1-9]\d?(?:\s*,\s*[1-9]\d?)*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AllRegex = new(@"\ball\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<MatchEntity> Resolve(string text, IReadOnlyList<MatchEntity> matches)
    {
        if (matches.Count == 0) return Array.Empty<MatchEntity>();

        if (AllRegex.IsMatch(text)) return matches.ToList();

        var indices = ParseIndices(text, matches.Count);
        if (indices.Count > 0) return indices.Select(i => matches[i]).ToList();

        var fragmentHit = MatchByPhoneFragment(text, matches);
        return fragmentHit is null ? Array.Empty<MatchEntity>() : [fragmentHit];
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

    private static MatchEntity? MatchByPhoneFragment(string text, IReadOnlyList<MatchEntity> matches)
    {
        // Prefer a full-phone hit before falling back to last-4 — otherwise an earlier
        // entry's "0000" suffix would shadow a later entry whose full number is in the text.
        var full = matches.FirstOrDefault(m => text.Contains(m.ProviderPhone, StringComparison.Ordinal));
        if (full is not null) return full;
        return matches.FirstOrDefault(m =>
            m.ProviderPhone.Length >= 4 && text.Contains(m.ProviderPhone[^4..], StringComparison.Ordinal));
    }
}
