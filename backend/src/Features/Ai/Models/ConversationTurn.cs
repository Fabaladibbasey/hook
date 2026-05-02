namespace Hook.Features.Ai.Models;

public enum TurnRole { User, System }

public sealed record ConversationTurn(TurnRole Role, string Text);

public sealed record ReplyContext(
    string Purpose,
    IReadOnlyList<ConversationTurn> RecentTurns,
    string LanguageHint = "en",
    IReadOnlyDictionary<string, string>? Facts = null);
