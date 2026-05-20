using System.Collections.Frozen;

namespace Hook.Features.Ai.Models;

public enum TurnRole { User, System }

public sealed record ConversationTurn(TurnRole Role, string Text);

public sealed record ReplyContext(
    string Purpose,
    IReadOnlyList<ConversationTurn> RecentTurns,
    string LanguageHint = "en")
{
    /// <summary>
    /// Optional structured facts passed to the LLM as a JSON block. Always non-null —
    /// supply via object initializer (e.g. <c>{ Facts = new Dictionary&lt;...&gt; { ... } }</c>).
    /// Empty default is a cached singleton; consumers may safely treat Count == 0 as "no facts".
    /// </summary>
    public IReadOnlyDictionary<string, string> Facts { get; init; } = FrozenDictionary<string, string>.Empty;
}
