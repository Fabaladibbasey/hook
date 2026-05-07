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
}
