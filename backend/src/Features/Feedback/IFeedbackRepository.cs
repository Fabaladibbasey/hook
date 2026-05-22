using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;

namespace Hook.Features.Feedback;

public interface IFeedbackRepository
{
    Task<MatchFeedback?> GetLatestPendingForClientAsync(string clientPhone, CancellationToken ct = default);
    Task<MatchFeedback?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<MatchFeedback?> GetPendingAsync(Guid matchId, FeedbackStep step, CancellationToken ct = default);
    Task<MatchFeedback?> GetLatestByMatchAndStepAsync(Guid matchId, FeedbackStep step, CancellationToken ct = default);
    Task<bool> TryClaimPendingAsync(
        Guid feedbackId,
        FeedbackAnswer answer,
        DateTimeOffset now,
        CancellationToken ct = default);
    Task<bool> TryClaimPendingWithEtaAsync(
        Guid feedbackId,
        FeedbackAnswer answer,
        DateTimeOffset etaUtc,
        DateTimeOffset now,
        CancellationToken ct = default);
    Task AddAsync(MatchFeedback feedback, CancellationToken ct = default);
    Task<bool> TryAddPendingAsync(MatchFeedback feedback, CancellationToken ct = default);
    Task<bool> DeletePendingAsync(Guid feedbackId, CancellationToken ct = default);
    Task<ProviderStats?> GetStatsAsync(string providerPhone, CancellationToken ct = default);
    Task UpsertStatsAsync(ProviderStats stats, CancellationToken ct = default);
    Task DeleteStatsAsync(string providerPhone, CancellationToken ct = default);
}
