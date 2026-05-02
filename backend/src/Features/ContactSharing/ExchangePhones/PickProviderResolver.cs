using System.Text.RegularExpressions;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;

namespace Hook.Features.ContactSharing.ExchangePhones;

public static class PickProviderResolver
{
    public static readonly Regex PickRegex = new(
        @"\b(?:pick\s*)?#?\s*([1-9]\d?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static MatchEntity? Resolve(string text, IReadOnlyList<MatchEntity> matches)
    {
        var index = TryParseIndex(text, matches.Count);
        return index is not null
            ? matches.Skip(index.Value).FirstOrDefault()
            : MatchByPhoneFragment(text, matches);
    }

    private static int? TryParseIndex(string text, int count)
    {
        var match = PickRegex.Match(text);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, out var n)) return null;
        return (n >= 1 && n <= count) ? n - 1 : null;
    }

    private static MatchEntity? MatchByPhoneFragment(string text, IReadOnlyList<MatchEntity> matches) =>
        matches.FirstOrDefault(m =>
            text.Contains(m.ProviderPhone, StringComparison.Ordinal) ||
            (m.ProviderPhone.Length >= 4 && text.Contains(m.ProviderPhone[^4..], StringComparison.Ordinal)));
}
