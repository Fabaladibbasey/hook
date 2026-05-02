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
            .ToListAsync(ct);

    public async Task AddAsync(Match match, CancellationToken ct = default) =>
        await db.Matches.AddAsync(match, ct);

    public Task AddRangeAsync(IEnumerable<Match> matches, CancellationToken ct = default) =>
        db.Matches.AddRangeAsync(matches, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
