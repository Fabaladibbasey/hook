using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;

namespace Hook.Features.Feedback;

public interface IFeedbackRepository
{
    Task<MatchFeedback?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<MatchFeedback?> GetPendingAsync(Guid matchId, FeedbackStep step, CancellationToken ct = default);
    Task<MatchFeedback?> GetLatestByMatchAndStepAsync(Guid matchId, FeedbackStep step, CancellationToken ct = default);

    // Flush mutations on a tracked aggregate. Returns false on Version concurrency-token
    // loss for THE passed aggregate (another writer claimed the row first). On loss the
    // conflicting entry is reloaded from the DB (State -> Unchanged) so the outer
    // AutoApplyTransactions commit does not re-issue the losing UPDATE and throw again.
    // Conflicts on unrelated entities in the same scoped HookDbContext rethrow so the
    // outer Wolverine tx rolls back instead of swallowing cross-entity data loss.
    // Outbox envelopes published BEFORE this call still commit with the outer tx; the
    // false return signals the caller to short-circuit further mutations on the same
    // aggregate.
    Task<bool> TrySaveAsync(MatchFeedback aggregate, CancellationToken ct = default);

    Task<bool> TryAddPendingAsync(MatchFeedback feedback, CancellationToken ct = default);
    Task<bool> DeletePendingAsync(Guid feedbackId, CancellationToken ct = default);
    Task<ProviderStats?> GetStatsAsync(string providerPhone, CancellationToken ct = default);
    Task UpsertStatsAsync(ProviderStats stats, CancellationToken ct = default);
    Task DeleteStatsAsync(string providerPhone, CancellationToken ct = default);
}
