using System.Text;

namespace Hook.Shared.Whatsapp;

/// <summary>
/// Wraps user-supplied free-text (ServiceRequest.Description) for safe inclusion in
/// outbound provider WhatsApp messages. Sanitizes control / BiDi / zero-width chars,
/// caps length, collapses whitespace, and frames with unspoofable markers so the
/// provider can always distinguish platform copy from forwarded client copy.
/// </summary>
public static class RequestDetailsFormatter
{
    private const int MaxChars = 280;
    private const string OpenMarker = "— client message (forwarded, not verified) —";
    private const string CloseMarker = "— end client message —";

    public static string AppendIfPresent(string body, string? description)
    {
        var clean = Sanitize(description);
        return string.IsNullOrEmpty(clean)
            ? body
            : $"{body}\n\n{OpenMarker}\n{clean}\n{CloseMarker}";
    }

    public static string Sanitize(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;

        var sb = new StringBuilder(description.Length);
        foreach (var ch in description)
        {
            if (ch == '\n' || ch == '\r' || ch == '\t') { sb.Append(ch); continue; }
            if (ch < 0x20 || ch == 0x7F) continue;                          // ASCII control
            if (ch is >= '​' and <= '‏') continue;                // zero-width / RLM/LRM
            if (ch is >= '‪' and <= '‮') continue;                // BiDi embedding/override
            if (ch is >= '⁦' and <= '⁩') continue;                // BiDi isolates
            sb.Append(ch);
        }

        var collapsed = CollapseNewlines(sb.ToString().Trim());
        collapsed = collapsed
            .Replace(OpenMarker, "[marker]", StringComparison.Ordinal)
            .Replace(CloseMarker, "[marker]", StringComparison.Ordinal);

        return collapsed.Length > MaxChars ? collapsed[..MaxChars] + "…" : collapsed;
    }

    private static string CollapseNewlines(string s)
    {
        var sb = new StringBuilder(s.Length);
        var lastWasNewline = false;
        foreach (var ch in s)
        {
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                if (!lastWasNewline) sb.Append('\n');
                lastWasNewline = true;
                continue;
            }
            if (lastWasNewline && char.IsWhiteSpace(ch)) continue;
            sb.Append(ch);
            lastWasNewline = false;
        }
        return sb.ToString();
    }
}
