using Hook.Features.Ai.Models;

namespace Hook.Features.Ai;

public interface IConversationAi
{
    Task<IntentDetectionResult> DetectIntentAsync(string userMessage, CancellationToken ct = default);

    Task<ServiceExtractionResult> ExtractServicesAsync(string userMessage, CancellationToken ct = default);

    Task<ServiceJudgeResult> JudgeServiceMatchAsync(
        string proposedSlug,
        IReadOnlyList<string> candidateSlugs,
        CancellationToken ct = default);

    // Returns one of `rootCandidates` if the proposal is plausibly a specialization
    // of it (e.g. cardiology ⊂ doctor). Returns null on no-fit, self-match, or
    // out-of-candidate hallucination. Throws only on transport failure; the
    // dispatcher (JudgeParentSlugDispatchHandler) catches and stays root.
    Task<string?> JudgeParentSlugAsync(
        string proposedSlug,
        IReadOnlyList<string> rootCandidates,
        IReadOnlyList<string> rawExamples,
        CancellationToken ct = default);

    Task<string> GenerateReplyAsync(ReplyContext context, CancellationToken ct = default);

    Task<LanguageDetectionResult> DetectLanguageAsync(string userMessage, CancellationToken ct = default);

    // Extracts a future point-in-time the user has stated for when a job will be
    // done. Returns null when:
    // - the message contains no parseable future time
    // - the JSON response is missing `etaUtc` or it is null/empty
    // - the parsed timestamp is at or before `now` (no point scheduling a recheck
    //   for a moment that has already passed)
    // - the parsed string is not a valid ISO-8601 instant
    // `now` lets callers ground relative phrases like "in 3 hours". The system
    // prompt currently assumes UTC clock-times when parsing absolute references —
    // non-UTC clients may see a wrong-day recheck (tracked separately).
    Task<DateTimeOffset?> ExtractEtaAsync(string userMessage, DateTimeOffset now, CancellationToken ct = default);
}
