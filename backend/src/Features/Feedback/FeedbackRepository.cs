using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.Feedback;

public sealed class FeedbackRepository(HookDbContext db) : IFeedbackRepository
{
    public Task<MatchFeedback?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.MatchFeedback.FirstOrDefaultAsync(f => f.Id == id, ct);

    public Task<MatchFeedback?> GetPendingAsync(Guid matchId, FeedbackStep step, CancellationToken ct = default) =>
        db.MatchFeedback.FirstOrDefaultAsync(
            f => f.MatchId == matchId && f.Step == step && f.Answer == FeedbackAnswer.Pending, ct);

    public Task<MatchFeedback?> GetLatestByMatchAndStepAsync(
        Guid matchId,
        FeedbackStep step,
        CancellationToken ct = default) =>
        db.MatchFeedback
            .Where(f => f.MatchId == matchId && f.Step == step)
            .OrderByDescending(f => f.PromptedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<bool> TryClaimPendingAsync(
        Guid feedbackId, FeedbackAnswer answer, DateTimeOffset now, CancellationToken ct = default)
    {
        var rows = await db.MatchFeedback
            .Where(f => f.Id == feedbackId && f.Answer == FeedbackAnswer.Pending)
            .ExecuteUpdateAsync(u => u
                .SetProperty(f => f.Answer, answer)
                .SetProperty(f => f.RepliedAt, now), ct);
        return rows == 1;
    }

    // Atomic claim variant for AwaitingEta — keeps the captured ETA on the same row
    // and the same UPDATE so a concurrent fire can't see the answer flip without the
    // ETA, or vice-versa.
    public async Task<bool> TryClaimPendingWithEtaAsync(
        Guid feedbackId,
        FeedbackAnswer answer,
        DateTimeOffset etaUtc,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var rows = await db.MatchFeedback
            .Where(f => f.Id == feedbackId && f.Answer == FeedbackAnswer.Pending)
            .ExecuteUpdateAsync(u => u
                .SetProperty(f => f.Answer, answer)
                .SetProperty(f => f.RepliedAt, now)
                .SetProperty(f => f.EtaUtc, etaUtc), ct);
        return rows == 1;
    }

    public async Task<bool> TryRescheduleAsync(
        Guid feedbackId, DateTimeOffset now, CancellationToken ct = default)
    {
        var rows = await db.MatchFeedback
            .Where(f => f.Id == feedbackId && f.Answer == FeedbackAnswer.Pending)
            .ExecuteUpdateAsync(u => u
                .SetProperty(f => f.Step1RecheckCount, f => f.Step1RecheckCount + 1)
                .SetProperty(f => f.PromptedAt, now), ct);
        return rows == 1;
    }

    public async Task<bool> TryClaimNoReasonAsync(
        Guid feedbackId, string? noReason, DateTimeOffset now, CancellationToken ct = default)
    {
        var rows = await db.MatchFeedback
            .Where(f => f.Id == feedbackId && f.Answer == FeedbackAnswer.Pending)
            .ExecuteUpdateAsync(u => u
                .SetProperty(f => f.Answer, FeedbackAnswer.NoReasonCaptured)
                .SetProperty(f => f.RepliedAt, now)
                .SetProperty(f => f.NoReason, noReason), ct);
        return rows == 1;
    }

    public async Task<bool> TryRepromptPendingAsync(
        Guid feedbackId, DateTimeOffset now, TimeSpan minGap, CancellationToken ct = default)
    {
        var cutoff = now - minGap;
        var rows = await db.MatchFeedback
            .Where(f => f.Id == feedbackId
                     && f.Answer == FeedbackAnswer.Pending
                     && f.PromptedAt <= cutoff)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.PromptedAt, now), ct);
        return rows == 1;
    }

    public Task<bool> TryAddPendingAsync(MatchFeedback feedback, CancellationToken ct = default) =>
        db.TryInsertUniqueAsync(feedback, ct,
            FeedbackConstants.PendingUniqueIndexName,
            FeedbackConstants.RequestStep1UniqueIndexName);

    public async Task<bool> DeletePendingAsync(Guid feedbackId, CancellationToken ct = default)
    {
        var rows = await db.MatchFeedback
            .Where(f => f.Id == feedbackId && f.Answer == FeedbackAnswer.Pending)
            .ExecuteDeleteAsync(ct);
        return rows == 1;
    }

    public Task<ProviderStats?> GetStatsAsync(string providerPhone, CancellationToken ct = default) =>
        db.ProviderStats.FirstOrDefaultAsync(s => s.ProviderPhone == providerPhone, ct);

    public async Task UpsertStatsAsync(ProviderStats stats, CancellationToken ct = default)
    {
        var entry = db.Entry(stats);
        if (entry.State != EntityState.Detached) return;

        var existing = await db.ProviderStats.FindAsync([stats.ProviderPhone], ct);
        if (existing is null)
        {
            db.ProviderStats.Add(stats);
        }
        else if (!ReferenceEquals(existing, stats))
        {
            db.Entry(existing).CurrentValues.SetValues(stats);
        }
    }

    public Task DeleteStatsAsync(string providerPhone, CancellationToken ct = default) =>
        db.ProviderStats.Where(s => s.ProviderPhone == providerPhone).ExecuteDeleteAsync(ct);
}
