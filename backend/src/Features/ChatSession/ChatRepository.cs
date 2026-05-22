using Hook.Features.ChatSession.AccessLog;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Shared.Persistence;
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

    public async Task<IReadOnlyList<ChatParticipant>> GetParticipantsAsync(
        Guid chatId,
        CancellationToken ct = default) =>
        await db.ChatParticipants.Where(p => p.ChatId == chatId).ToListAsync(ct);

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(
        Guid chatId,
        int take,
        CancellationToken ct = default)
    {
        var clamped = Math.Clamp(take, 1, 500);
        var rows = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.ChatId == chatId)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Sequence)
            .Take(clamped)
            .ToListAsync(ct);
        rows.Reverse();
        return rows;
    }

    public async Task AddSessionAsync(SessionAggregate.ChatSession session, CancellationToken ct = default) =>
        await db.ChatSessions.AddAsync(session, ct);

    public async Task AddParticipantAsync(ChatParticipant participant, CancellationToken ct = default) =>
        await db.ChatParticipants.AddAsync(participant, ct);

    public Task AddParticipantsAsync(IEnumerable<ChatParticipant> participants, CancellationToken ct = default) =>
        db.ChatParticipants.AddRangeAsync(participants, ct);

    public Task<bool> TryAddMessageAsync(ChatMessage message, CancellationToken ct = default) =>
        db.TryInsertUniqueAsync(message, ct, ChatHubConstants.ChatMessagesPrimaryKey);

    public async Task AddAccessLogAsync(ChatAccessLog log, CancellationToken ct = default) =>
        await db.ChatAccessLogs.AddAsync(log, ct);
}
