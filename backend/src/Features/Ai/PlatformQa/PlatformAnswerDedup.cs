namespace Hook.Features.Ai.PlatformQa;

// Tiny per-(phone, question-hash) dedup row that suppresses duplicate Ollama
// inference when a Wolverine retry replays AnswerPlatformQuestionCommand after
// a transient publish failure. Pruned by RetentionSweeper.
public sealed class PlatformAnswerDedup
{
    public string Phone { get; private init; } = string.Empty;
    public long QuestionHash { get; private init; }
    public DateTimeOffset AnsweredAt { get; private set; }

    public static PlatformAnswerDedup Stamp(string phone, long questionHash, DateTimeOffset now) =>
        new() { Phone = phone, QuestionHash = questionHash, AnsweredAt = now };
}
