using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.Matching.MatchAggregate;

public sealed class MatchRepository(HookDbContext db) : IMatchRepository
{
    public Task<Match?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Matches.FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<IReadOnlyList<Match>> GetForRequestAsync(Guid requestId, CancellationToken ct = default) =>
        await db.Matches
            .Where(m => m.RequestId == requestId)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.DistanceKm)
            .ThenBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Match>> GetPickedForRequestAsync(Guid requestId, CancellationToken ct = default) =>
        await db.Matches
            .Where(m => m.RequestId == requestId && m.PickedAt != null)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.DistanceKm)
            .ThenBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);

    public async Task AddAsync(Match match, CancellationToken ct = default) =>
        await db.Matches.AddAsync(match, ct);

    public Task AddRangeAsync(IEnumerable<Match> matches, CancellationToken ct = default) =>
        db.Matches.AddRangeAsync(matches, ct);

    public async Task<bool> TryClaimPickAsync(PickClaim claim, CancellationToken ct = default)
    {
        // Identity guard: only the request's owning client may claim the match. Folded
        // into the UPDATE so an IDOR attempt from a forged matchId cannot succeed.
        var query = db.Matches.Where(m =>
            m.Id == claim.MatchId &&
            m.PickedAt == null &&
            db.ServiceRequests.Any(r => r.Id == m.RequestId && r.ClientPhone == claim.CallerClientPhone));

        // On the reveal-contact path, fold provider consent + TTL into the UPDATE so a
        // concurrent ShareContact=false / TTL expiry between PhoneExchanger's read and
        // this write cannot leak phones — the row count comes back zero instead.
        if (claim.RevealContact)
        {
            query = query.Where(m => db.ProviderAvailabilities.Any(p =>
                p.Phone == m.ProviderPhone &&
                p.ShareContact &&
                p.ExpiresAt > claim.Now));
        }

        var rows = await query.ExecuteUpdateAsync(u => u
            .SetProperty(m => m.ContactShared, claim.RevealContact)
            .SetProperty(m => m.PickedAt, claim.Now), ct);
        return rows == 1;
    }

    public async Task<bool> TryClaimChatRoutingAsync(Guid matchId, Guid chatId, CancellationToken ct = default)
    {
        var rows = await db.Matches
            .Where(m => m.Id == matchId && m.ChatId == null)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.ChatId, chatId), ct);
        return rows == 1;
    }
}
