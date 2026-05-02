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

    Task<string> GenerateReplyAsync(ReplyContext context, CancellationToken ct = default);

    Task<LanguageDetectionResult> DetectLanguageAsync(string userMessage, CancellationToken ct = default);
}
