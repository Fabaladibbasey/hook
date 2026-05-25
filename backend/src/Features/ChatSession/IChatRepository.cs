using Hook.Features.ChatSession.AccessLog;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hook.Features.ChatSession;

public sealed record ProductiveSilenceSnapshot(
    ChatSessionStatus Status,
    DateTimeOffset? ProductiveSilenceFiredAt,
    DateTimeOffset LastActivityAt);

public interface IChatRepository
{
    Task<SessionAggregate.ChatSession?> GetSessionAsync(Guid chatId, CancellationToken ct = default);
    Task<ChatParticipant?> GetByTokenAsync(string token, CancellationToken ct = default);
    Task<ChatParticipant?> GetParticipantAsync(Guid participantId, CancellationToken ct = default);
    // Single-column existence probe — `SELECT 1 ... WHERE Id=@pid AND CurrentSessionId=@sid`.
    // Used by ChatHub.EndChat to validate the live SignalR session against the row
    // without re-fetching the full ChatParticipant projection.
    Task<bool> IsCurrentSessionAsync(Guid participantId, Guid sessionId, CancellationToken ct = default);
    Task<ChatParticipant?> GetPeerAsync(Guid chatId, Guid exceptParticipantId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatParticipant>> GetParticipantsAsync(Guid chatId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid chatId, int take, CancellationToken ct = default);
    // Counts messages per ChatParticipantRole for the chat. Used by the
    // productive-silence trigger to require both sides actually conversed
    // before firing Step1 feedback early.
    Task<(int ClientCount, int ProviderCount)> GetMessageCountByRoleAsync(
        Guid chatId, CancellationToken ct = default);
    // Atomic productive-silence gate. Returns true on the winning insert; losing
    // races (or already-fired) return false so the caller skips the Step1 publish.
    Task<bool> TryMarkProductiveSilenceAsync(
        Guid chatId, DateTimeOffset now, CancellationToken ct = default);
    // Tracking-free snapshot of the three fields the productive-silence handler
    // gates on. Avoids hydrating the full ChatSession + binding events for what
    // is a read-only health check.
    Task<ProductiveSilenceSnapshot?> GetProductiveSilenceSnapshotAsync(
        Guid chatId, CancellationToken ct = default);
    Task AddSessionAsync(SessionAggregate.ChatSession session, CancellationToken ct = default);
    Task AddParticipantAsync(ChatParticipant participant, CancellationToken ct = default);
    Task AddParticipantsAsync(IEnumerable<ChatParticipant> participants, CancellationToken ct = default);
    Task<bool> TryAddMessageAsync(ChatMessage message, CancellationToken ct = default);
    Task AddAccessLogAsync(ChatAccessLog log, CancellationToken ct = default);

    /// <summary>
    /// Flushes tracked chat-aggregate mutations. Returns false on concurrency-token
    /// loss (another writer mutated the same <c>ChatParticipant</c>/<c>ChatSession</c>
    /// row between this scope's load and commit). Callers surface the loss as
    /// <c>SessionRevoked</c>/<c>SessionEnded</c> so the racing tab is asked to refresh
    /// rather than overwriting fresh state with stale.
    /// On loss the implementation clears the EF change tracker wholesale, so callers
    /// MUST NOT hold tracked writes on sibling entities (other aggregates loaded in
    /// the same scope) — those mutations would silently drop with the conflicting one.
    /// </summary>
    Task<bool> TryCommitAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens an explicit EF transaction. Call this ONLY when the handler is
    /// <c>[NonTransactional]</c> (AutoApplyTransactions is off) AND the happy path
    /// stages 2+ mutations that must roll back together on conflict — i.e. an
    /// insert + sequence advance + touch where a concurrency-token loss on the
    /// post-insert state must also undo the savepoint-protected insert.
    /// Handlers with a single mutation and no reject-path BEGIN/COMMIT cost should
    /// stay on the default AutoApplyTransactions path + <see cref="TryCommitAsync"/>
    /// (see <c>PublishParticipantKeyHandler</c>) — opening an explicit tx there
    /// just doubles up on EF's transaction handling.
    /// </summary>
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default);
}
