using Hook.Features.Ai.Models;

namespace Hook.Features.Ai;

// AI-stage methods (DetectIntent / ExtractServices / JudgeServiceMatch /
// JudgeParentSlug / ExtractEta) absorb transport + parsing failures inside the
// adapter and return a documented neutral fallback so callers stay try-catch-free.
// Outer-token cancellation rethrows so Wolverine's shutdown OCE policy can
// discard cleanly. The remaining methods (GenerateReply, DetectLanguage) keep
// the original throw-on-failure contract; their callers route through their
// own absorber (AiReplyHelper for reply, language-detection callers handle
// directly). PingAsync is the dedicated non-absorbing probe used by
// AiReadinessProbe / AiWarmupHostedService — it throws on transport failure
// so /readyz and warmup can tell "healthy" from "model unreachable".
public interface IConversationAi
{
    // Health probe — throws on transport / parsing failure. Healthy iff returns.
    Task PingAsync(CancellationToken ct = default);

    // Returns IntentDetectionResult(Unknown, 0, "en", "exception") on AI failure.
    Task<IntentDetectionResult> DetectIntentAsync(string userMessage, CancellationToken ct = default);

    // Returns an empty ServiceExtractionResult on AI failure.
    Task<ServiceExtractionResult> ExtractServicesAsync(string userMessage, CancellationToken ct = default);

    // Returns ServiceJudgeResult(MatchedSlug: "", IsNew: true, ProposedSlug:
    // proposedSlug) on AI failure — "assume new, keep moving" matches the
    // pre-consolidation handler-catch semantics so the funnel does not halt.
    Task<ServiceJudgeResult> JudgeServiceMatchAsync(
        string proposedSlug,
        IReadOnlyList<string> candidateSlugs,
        CancellationToken ct = default);

    // Returns one of `rootCandidates` if the proposal is plausibly a specialization
    // of it (e.g. cardiology ⊂ doctor). Returns null on no-fit, self-match,
    // out-of-candidate hallucination, OR AI failure — the dispatcher stays root.
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
    // - the AI call fails (transport or parsing). The caller falls back to the
    //   fixed Step2 recheck delay.
    // `now` lets callers ground relative phrases like "in 3 hours". The system
    // prompt currently assumes UTC clock-times when parsing absolute references —
    // non-UTC clients may see a wrong-day recheck (tracked separately).
    Task<DateTimeOffset?> ExtractEtaAsync(string userMessage, DateTimeOffset now, CancellationToken ct = default);
}
