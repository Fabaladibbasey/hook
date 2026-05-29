using System.Collections.Frozen;

namespace Hook.Features.Ai.PlatformQa;

// Two-tier locale lookup: 3-letter code (e.g. "mnk", "ff") wins over 2-letter
// (en/fr/ar/wo/es/pt). Falls back to defaultEn for empty / short / unknown
// locales. Dictionary comparer choice is the caller's — LocaleValidator.Sanitize
// outputs lowercase, so Ordinal is the right default.
internal static class LocalisedString
{
    public static string For(string? locale, FrozenDictionary<string, string> table, string defaultEn)
    {
        if (string.IsNullOrEmpty(locale) || locale.Length < 2) return defaultEn;
        if (locale.Length >= 3 && table.TryGetValue(locale[..3], out var three))
            return three;
        return table.TryGetValue(locale[..2], out var two) ? two : defaultEn;
    }
}
