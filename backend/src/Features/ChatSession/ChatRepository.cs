using Hook.Features.ChatSession.AccessLog;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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

    public async Task<(int ClientCount, int ProviderCount)> GetMessageCountByRoleAsync(
        Guid chatId, CancellationToken ct = default)
    {
        // Two grouped counts in one round-trip. AsNoTracking — we never write back
        // through these projections.
        var counts = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.ChatId == chatId)
            .Join(db.ChatParticipants, m => m.ParticipantId, p => p.Id, (_, p) => p.Role)
            .GroupBy(r => r)
            .Select(g => new { Role = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int client = 0, provider = 0;
        foreach (var c in counts)
        {
            if (c.Role == ChatParticipantRole.Client) client = c.Count;
            else if (c.Role == ChatParticipantRole.Provider) provider = c.Count;
        }
        return (client, provider);
    }

    public Task<ProductiveSilenceSnapshot?> GetProductiveSilenceSnapshotAsync(
        Guid chatId, CancellationToken ct = default) =>
        db.ChatSessions
            .AsNoTracking()
            .Where(c => c.Id == chatId)
            .Select(c => new ProductiveSilenceSnapshot(
                c.Status, c.ProductiveSilenceFiredAt, c.LastActivityAt))
            .FirstOrDefaultAsync(ct);

    public async Task<bool> TryMarkProductiveSilenceAsync(
        Guid chatId, DateTimeOffset now, CancellationToken ct = default)
    {
        var rows = await db.ChatSessions
            .Where(c => c.Id == chatId
                     && c.Status == ChatSessionStatus.Active
                     && c.ProductiveSilenceFiredAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.ProductiveSilenceFiredAt, now), ct);
        return rows == 1;
    }

    public async Task AddSessionAsync(SessionAggregate.ChatSession session, CancellationToken ct = default) =>
        await db.ChatSessions.AddAsync(session, ct);

    public async Task AddParticipantAsync(ChatParticipant participant, CancellationToken ct = default) =>
        await db.ChatParticipants.AddAsync(participant, ct);

    public Task AddParticipantsAsync(IEnumerable<ChatParticipant> participants, CancellationToken ct = default) =>
        db.ChatParticipants.AddRangeAsync(participants, ct);

    public Task<bool> TryAddMessageAsync(ChatMessage message, CancellationToken ct = default) =>
        db.TryInsertUniqueAsync(message, ct,
            ChatHubConstants.ChatMessagesPrimaryKey,
            ChatHubConstants.SequenceUniqueIndexName);

    public async Task AddAccessLogAsync(ChatAccessLog log, CancellationToken ct = default) =>
        await db.ChatAccessLogs.AddAsync(log, ct);

    public async Task<bool> TryCommitAsync(CancellationToken ct = default)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Drop tracked state so a follow-up SaveChanges (e.g. the
            // AutoApplyTransactions commit when this method is called inside an
            // outer-tx handler) does not retry the same losing UPDATE.
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
        db.Database.BeginTransactionAsync(ct);
}
