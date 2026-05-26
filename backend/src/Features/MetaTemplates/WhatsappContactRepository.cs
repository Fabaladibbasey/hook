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
        // Single-roundtrip upsert: ON CONFLICT keeps the greater LastInboundAt so an
        // out-of-order delivery from WhatsApp cannot move the timestamp backwards.
        // Bypasses the change tracker (the alternative was FindAsync + SaveChanges,
        // two roundtrips on every inbound).
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO whatsapp_contacts ("Phone", "LastInboundAt") VALUES ({phone}, {at})
            ON CONFLICT ("Phone") DO UPDATE
              SET "LastInboundAt" = GREATEST(EXCLUDED."LastInboundAt", whatsapp_contacts."LastInboundAt");
            """, ct);
    }

    public async Task<ContactTipState?> GetForTipsAsync(string phone, CancellationToken ct = default)
    {
        var row = await db.WhatsappContacts.AsNoTracking()
            .Where(c => c.Phone == phone)
            .Select(c => new { c.LastTipKey, c.LastTipAt })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : new ContactTipState(row.LastTipKey, row.LastTipAt);
    }

    public async Task RecordTipAsync(string phone, string tipKey, DateTimeOffset at, CancellationToken ct = default)
    {
        // UpsertInboundAsync always lands first on the same inbound, so the row is
        // guaranteed to exist when a tip dispatch reaches us. A plain UPDATE is
        // sufficient; matched-rows = 0 is silently ignored (best-effort cooldown).
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE whatsapp_contacts
               SET "LastTipKey" = {tipKey}, "LastTipAt" = {at}
             WHERE "Phone" = {phone};
            """, ct);
    }
}
