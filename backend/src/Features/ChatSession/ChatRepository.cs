using Hook.Features.ChatSession.AccessLog;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.ChatSession;

public sealed class ChatRepository(HookDbContext db) : IChatRepository
{
    public Task<SessionAggregate.ChatSession?> GetSessionAsync(Guid chatId, CancellationToken ct = default) =>
        db.ChatSessions.FirstOrDefaultAsync(c => c.Id == chatId, ct);

    public Task<ChatParticipant?> GetByTokenAsync(string token, CancellationToken ct = default) =>
        db.ChatParticipants.FirstOrDefaultAsync(p => p.Token == token, ct);

    public Task<ChatParticipant?> GetParticipantAsync(Guid participantId, CancellationToken ct = default) =>
        db.ChatParticipants.FirstOrDefaultAsync(p => p.Id == participantId, ct);

    public Task<ChatParticipant?> GetPeerAsync(Guid chatId, Guid exceptParticipantId, CancellationToken ct = default) =>
        db.ChatParticipants.FirstOrDefaultAsync(
            p => p.ChatId == chatId && p.Id != exceptParticipantId, ct);

    public async Task<IReadOnlyList<ChatParticipant>> GetParticipantsAsync(Guid chatId, CancellationToken ct = default) =>
        await db.ChatParticipants.Where(p => p.ChatId == chatId).ToListAsync(ct);

    public Task<ChatDeviceKey?> GetDeviceKeyAsync(Guid participantId, Guid deviceId, CancellationToken ct = default) =>
        db.ChatDeviceKeys.FirstOrDefaultAsync(k => k.ParticipantId == participantId && k.DeviceId == deviceId, ct);

    public async Task<IReadOnlyList<ChatDeviceKey>> GetDeviceKeysAsync(Guid chatId, CancellationToken ct = default) =>
        await db.ChatDeviceKeys.Where(k => k.ChatId == chatId).ToListAsync(ct);

    public async Task UpsertDeviceKeyAsync(Guid chatId, Guid participantId, Guid deviceId, byte[] publicKey, DateTimeOffset now, CancellationToken ct = default)
    {
        var existing = await db.ChatDeviceKeys
            .FirstOrDefaultAsync(k => k.ParticipantId == participantId && k.DeviceId == deviceId, ct);
        if (existing is null)
        {
            await db.ChatDeviceKeys.AddAsync(new ChatDeviceKey
            {
                ChatId = chatId,
                ParticipantId = participantId,
                DeviceId = deviceId,
                PublicKey = publicKey,
                FirstSeenAt = now,
                LastSeenAt = now
            }, ct);
        }
        else
        {
            existing.PublicKey = publicKey;
            existing.LastSeenAt = now;
        }
    }

    public async Task<IReadOnlyList<(ChatMessage Header, ChatMessageRecipient Envelope)>> GetMessagesForDeviceAsync(Guid chatId, Guid deviceId, int take, CancellationToken ct = default)
    {
        var rows = await (
            from m in db.ChatMessages
            where m.ChatId == chatId
            join r in db.ChatMessageRecipients on m.Id equals r.MessageId
            where r.RecipientDeviceId == deviceId
            orderby m.CreatedAt descending
            select new { Header = m, Envelope = r }
        ).Take(take).ToListAsync(ct);
        return rows.OrderBy(x => x.Header.CreatedAt).Select(x => (x.Header, x.Envelope)).ToList();
    }

    public async Task AddSessionAsync(SessionAggregate.ChatSession session, CancellationToken ct = default) =>
        await db.ChatSessions.AddAsync(session, ct);

    public async Task AddParticipantAsync(ChatParticipant participant, CancellationToken ct = default) =>
        await db.ChatParticipants.AddAsync(participant, ct);

    public Task AddParticipantsAsync(IEnumerable<ChatParticipant> participants, CancellationToken ct = default) =>
        db.ChatParticipants.AddRangeAsync(participants, ct);

    public async Task AddMessageAsync(ChatMessage message, CancellationToken ct = default) =>
        await db.ChatMessages.AddAsync(message, ct);

    public async Task AddAccessLogAsync(ChatAccessLog log, CancellationToken ct = default) =>
        await db.ChatAccessLogs.AddAsync(log, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
