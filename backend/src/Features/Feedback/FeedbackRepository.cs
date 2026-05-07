using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.Feedback;

public sealed class FeedbackRepository(HookDbContext db) : IFeedbackRepository
{
    public Task<MatchFeedback?> GetLatestPendingForClientAsync(string clientPhone, CancellationToken ct = default) =>
        db.MatchFeedback
            .Where(f => f.Answer == FeedbackAnswer.Pending
                && db.Matches.Any(m => m.Id == f.MatchId
                    && db.ServiceRequests.Any(r => r.Id == m.RequestId && r.ClientPhone == clientPhone)))
            .OrderByDescending(f => f.PromptedAt)
            .FirstOrDefaultAsync(ct);

    public Task<MatchFeedback?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.MatchFeedback.FirstOrDefaultAsync(f => f.Id == id, ct);

    public async Task AddAsync(MatchFeedback feedback, CancellationToken ct = default) =>
        await db.MatchFeedback.AddAsync(feedback, ct);

    public Task<ProviderStats?> GetStatsAsync(string providerPhone, CancellationToken ct = default) =>
        db.ProviderStats.FirstOrDefaultAsync(s => s.ProviderPhone == providerPhone, ct);

    public async Task UpsertStatsAsync(ProviderStats stats, CancellationToken ct = default)
    {
        var entry = db.Entry(stats);
        if (entry.State == EntityState.Detached)
        {
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
        await db.SaveChangesAsync(ct);
    }

    public Task DeleteStatsAsync(string providerPhone, CancellationToken ct = default) =>
        db.ProviderStats.Where(s => s.ProviderPhone == providerPhone).ExecuteDeleteAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
