using System.Text.RegularExpressions;

namespace Hook.Features.Ai.PlatformQa;

// BCP-47-shaped locale guard. The classifier emits a free-text language code
// that we interpolate into the platform-answer prompt outside any safety fence,
// so a payload like "en\nNew instruction" would smuggle text into the trusted
// slot. Sanitize accepts only `ll` / `lll` / `ll-RR` shapes and falls back to
// "en" for anything else.
internal static class LocaleValidator
{
    private static readonly Regex BcpRx = new(
        "^[a-z]{2,3}(-[A-Z]{2})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "en";
        var trimmed = raw.Trim();
        if (trimmed.Length > 5) return "en";
        return BcpRx.IsMatch(trimmed) ? trimmed : "en";
    }
}
