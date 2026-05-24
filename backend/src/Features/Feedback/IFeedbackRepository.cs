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
    // Atomic reschedule: bump RecheckCount and refresh PromptedAt so the next
    // reply window restarts. Row stays Pending so the recheck dispatcher can
    // find it. Returns false when the row has already been claimed.
    Task<bool> TryRescheduleAsync(
        Guid feedbackId,
        DateTimeOffset now,
        CancellationToken ct = default);
    // Atomic claim for the CaptureNoReason follow-up row.
    Task<bool> TryClaimNoReasonAsync(
        Guid feedbackId,
        string? noReason,
        DateTimeOffset now,
        CancellationToken ct = default);
    // Atomic Step1 re-prompt guard: refresh PromptedAt only when the last prompt
    // is older than `minGap`. Returns false when the gap has not elapsed (caller
    // skips the re-fire) or the row is no longer Pending.
    Task<bool> TryRepromptPendingAsync(
        Guid feedbackId,
        DateTimeOffset now,
        TimeSpan minGap,
        CancellationToken ct = default);
    Task AddAsync(MatchFeedback feedback, CancellationToken ct = default);
    Task<bool> TryAddPendingAsync(MatchFeedback feedback, CancellationToken ct = default);
    Task<bool> DeletePendingAsync(Guid feedbackId, CancellationToken ct = default);
    Task<ProviderStats?> GetStatsAsync(string providerPhone, CancellationToken ct = default);
    Task UpsertStatsAsync(ProviderStats stats, CancellationToken ct = default);
    Task DeleteStatsAsync(string providerPhone, CancellationToken ct = default);
}
