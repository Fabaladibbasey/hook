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

    public async Task<bool> TrySaveAsync(MatchFeedback aggregate, CancellationToken ct = default)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Scope the swallow to the passed aggregate. Inside a Wolverine handler
            // tx the scoped HookDbContext also tracks ProviderStats / ServiceRequest /
            // ChatSession / etc; a concurrency loss on any of those unrelated rows
            // is real data loss, not a feedback race — rethrow so the outer tx
            // rolls back and the message retries.
            foreach (var entry in ex.Entries)
                if (!ReferenceEquals(entry.Entity, aggregate)) throw;
            return false;
        }
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
