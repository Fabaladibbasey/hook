using Hook.Features.ChatSession.AccessLog;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;

namespace Hook.Features.ChatSession;

public interface IChatRepository
{
    Task<SessionAggregate.ChatSession?> GetSessionAsync(Guid chatId, CancellationToken ct = default);
    Task<ChatParticipant?> GetByTokenAsync(string token, CancellationToken ct = default);
    Task<ChatParticipant?> GetParticipantAsync(Guid participantId, CancellationToken ct = default);
    Task<ChatParticipant?> GetPeerAsync(Guid chatId, Guid exceptParticipantId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatParticipant>> GetParticipantsAsync(Guid chatId, CancellationToken ct = default);

    Task<ChatDeviceKey?> GetDeviceKeyAsync(Guid participantId, Guid deviceId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatDeviceKey>> GetDeviceKeysAsync(Guid chatId, CancellationToken ct = default);
    Task UpsertDeviceKeyAsync(Guid chatId, Guid participantId, Guid deviceId, byte[] publicKey, DateTimeOffset now, CancellationToken ct = default);
    Task<IReadOnlyList<(ChatMessage Header, ChatMessageRecipient Envelope)>> GetMessagesForDeviceAsync(Guid chatId, Guid deviceId, int take, CancellationToken ct = default);

    Task AddSessionAsync(SessionAggregate.ChatSession session, CancellationToken ct = default);
    Task AddParticipantAsync(ChatParticipant participant, CancellationToken ct = default);
    Task AddParticipantsAsync(IEnumerable<ChatParticipant> participants, CancellationToken ct = default);
    Task AddMessageAsync(ChatMessage message, CancellationToken ct = default);
    Task AddAccessLogAsync(ChatAccessLog log, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
