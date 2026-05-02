using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.MetaTemplates;

public sealed class WhatsappContactRepository(HookDbContext db) : IWhatsappContactRepository
{
    public async Task<DateTimeOffset?> GetLastInboundAtAsync(string phone, CancellationToken ct = default)
    {
        var existing = await db.WhatsappContacts.AsNoTracking().FirstOrDefaultAsync(c => c.Phone == phone, ct);
        return existing?.LastInboundAt;
    }

    public async Task UpsertInboundAsync(string phone, DateTimeOffset at, CancellationToken ct = default)
    {
        var incoming = new WhatsappContact { Phone = phone, LastInboundAt = at };
        await db.WhatsappContacts.UpsertAsync([phone], incoming, (e, d) =>
        {
            if (d.LastInboundAt > e.LastInboundAt) e.LastInboundAt = d.LastInboundAt;
        }, ct);
        await db.SaveChangesAsync(ct);
    }
}
