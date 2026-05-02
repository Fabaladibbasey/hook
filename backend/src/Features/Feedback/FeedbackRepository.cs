using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.Feedback;

public sealed class FeedbackRepository(HookDbContext db) : IFeedbackRepository
{
    public async Task<MatchFeedback?> GetLatestPendingForClientAsync(string clientPhone, CancellationToken ct = default)
    {
        var matchIds = await (
            from r in db.ServiceRequests
            join m in db.Matches on r.Id equals m.RequestId
            where r.ClientPhone == clientPhone
            select m.Id).ToListAsync(ct);

        if (matchIds.Count == 0) return null;

        return await db.MatchFeedback
            .Where(f => matchIds.Contains(f.MatchId) && f.Answer == FeedbackAnswer.Pending)
            .OrderByDescending(f => f.PromptedAt)
            .FirstOrDefaultAsync(ct);
    }

    public Task<MatchFeedback?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.MatchFeedback.FirstOrDefaultAsync(f => f.Id == id, ct);

    public async Task AddAsync(MatchFeedback feedback, CancellationToken ct = default) =>
        await db.MatchFeedback.AddAsync(feedback, ct);

    public Task<ProviderStats?> GetStatsAsync(string providerPhone, CancellationToken ct = default) =>
        db.ProviderStats.FirstOrDefaultAsync(s => s.ProviderPhone == providerPhone, ct);

    public async Task UpsertStatsAsync(ProviderStats stats, CancellationToken ct = default)
    {
        var existing = await db.ProviderStats.FindAsync([stats.ProviderPhone], ct);
        if (existing is null)
        {
            await db.ProviderStats.AddAsync(stats, ct);
        }
        else if (!ReferenceEquals(existing, stats))
        {
            db.Entry(existing).CurrentValues.SetValues(stats);
        }
        await db.SaveChangesAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
