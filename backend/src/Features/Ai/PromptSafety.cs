using System.Text.Json;
using System.Text.RegularExpressions;
using Hook.Features.Ai.Models;

namespace Hook.Features.Ai;

public static class PromptSafety
{
    private const string Open = "<user_input>";
    private const string Close = "</user_input>";

    // Patterns chosen for the qwen2.5:3b family used by this project. The goal is
    // to drop messages that *echo* obvious jailbreak markers — not to catch every
    // possible attack. Defence-in-depth: the model should already refuse, this
    // is a belt-and-braces server-side filter on inbound user text and outbound
    // model replies.
    private static readonly Regex JailbreakRx = new(
        @"\b(ignor\w* (all |any |the )?(previous|prior|above) (instructions?|prompts?|rules?))" +
        @"|((reveal|print|show|leak)\s+(?:\w+\s+){0,3}(system|developer)\s+(prompt|message|rules?))" +
        @"|<\|im_(start|end)\|>" +
        @"|(\byou are now (in )?(developer|admin|jailbreak|dan) mode\b)" +
        @"|(\b(in )?(developer|admin|jailbreak|dan) mode\b)" +
        @"|(\bact(?:ing)? as (an? )?(unrestricted|uncensored|jailbroken)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SlugRx = new(
        @"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    // Chat-template control tokens used by qwen / llama / chatml-style models.
    // If a user types these into a WhatsApp message they must NOT survive into the
    // model's input verbatim — even though Ollama re-tokenises content into the
    // model's chat template, leaking these tokens has been a viable jailbreak
    // path on several open models. Defanging is cheap; we replace `<|x|>` with
    // `<x>` so the visible meaning is preserved but the token boundary is broken.
    private static readonly Regex ControlTokensRx = new(
        @"<\|[^|>]{1,32}\|>", RegexOptions.Compiled);

    /// <summary>
    /// Wrap untrusted user text in delimiters so the system prompt can refer to it as data.
    /// Inner occurrences of the delimiter and chat-control tokens are mangled so the user
    /// cannot escape the fence or smuggle a fake role into the model's input.
    /// </summary>
    public static string Fence(string userText, int maxChars = 1000)
    {
        var safe = (userText ?? string.Empty)
            .Replace(Open, "<user input>", StringComparison.OrdinalIgnoreCase)
            .Replace(Close, "</user input>", StringComparison.OrdinalIgnoreCase);
        safe = ControlTokensRx.Replace(safe, m => "<" + m.Value[2..^2] + ">");
        if (safe.Length > maxChars) safe = safe[..maxChars] + "…";
        return $"{Open}\n{safe}\n{Close}";
    }

    /// <summary>
    /// JSON-encode conversation turns so newlines / role tokens in user text cannot
    /// forge a fake "assistant:" or "system:" turn in a flat transcript.
    /// </summary>
    public static string EncodeTurns(IEnumerable<ConversationTurn> turns) =>
        JsonSerializer.Serialize(turns.Select(t => new { role = t.Role.ToString(), text = t.Text }));

    public static bool IsLikelyJailbreak(string s) =>
        !string.IsNullOrEmpty(s) && JailbreakRx.IsMatch(s);

    public static bool LooksLikeSlug(string s) =>
        !string.IsNullOrEmpty(s) && s.Length <= 64 && SlugRx.IsMatch(s);
}
