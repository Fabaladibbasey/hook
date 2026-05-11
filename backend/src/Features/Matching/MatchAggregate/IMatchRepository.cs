namespace Hook.Features.Matching.MatchAggregate;

public sealed record PickClaim(
    Guid MatchId,
    string CallerClientPhone,
    bool RevealContact,
    DateTimeOffset Now);

public interface IMatchRepository
{
    Task<Match?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Match>> GetForRequestAsync(Guid requestId, CancellationToken ct = default);
    Task AddAsync(Match match, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<Match> matches, CancellationToken ct = default);
    Task<bool> TryClaimPickAsync(PickClaim claim, CancellationToken ct = default);
    Task<bool> TryClaimChatRoutingAsync(Guid matchId, Guid chatId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
