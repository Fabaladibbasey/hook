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

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid chatId, int take, CancellationToken ct = default) =>
        await db.ChatMessages
            .Where(m => m.ChatId == chatId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

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
