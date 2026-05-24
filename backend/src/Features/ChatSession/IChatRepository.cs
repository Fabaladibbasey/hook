using Hook.Features.ChatSession.AccessLog;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;

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
}
