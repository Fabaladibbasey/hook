namespace Hook.Features.Matching.MatchAggregate;

public interface IMatchRepository
{
    Task<Match?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Match>> GetForRequestAsync(Guid requestId, CancellationToken ct = default);
    Task AddAsync(Match match, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<Match> matches, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
