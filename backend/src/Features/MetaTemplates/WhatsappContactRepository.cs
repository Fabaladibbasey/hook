using Hook.Features.Tips;
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
            .Select(c => new { c.TipCooldowns })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : new ContactTipState(row.TipCooldowns);
    }

    public async Task RecordTipAsync(string phone, TipTrigger trigger, DateTimeOffset at, CancellationToken ct = default)
    {
        // UpsertInboundAsync always lands first on the same inbound, so the row is
        // guaranteed to exist when a tip dispatch reaches us. A plain UPDATE is
        // sufficient; matched-rows = 0 is silently ignored (best-effort cooldown).
        // Key is the trigger ordinal as a string — matches STJ's default enum-keyed
        // dict serialization, so the round-tripped dict honours the cooldown.
        var key = ((int)trigger).ToString();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $$"""
            UPDATE whatsapp_contacts
               SET "TipCooldowns" = jsonb_set(
                   COALESCE("TipCooldowns", '{}'::jsonb),
                   ARRAY[{{key}}],
                   to_jsonb({{at}}::timestamptz))
             WHERE "Phone" = {{phone}};
            """, ct);
    }

    public async Task<string> GetPreferredLocaleAsync(string phone, CancellationToken ct = default)
    {
        var locale = await db.WhatsappContacts.AsNoTracking()
            .Where(c => c.Phone == phone)
            .Select(c => c.PreferredLocale)
            .FirstOrDefaultAsync(ct);
        return locale ?? string.Empty;
    }

    public async Task UpdatePreferredLocaleAsync(string phone, string locale, CancellationToken ct = default)
    {
        // Inbound flow guarantees UpsertInboundAsync has already created the row
        // before the LLM classifier persists locale, so a plain UPDATE suffices.
        // Skipped silently when 0 rows match (mirrors RecordTipAsync best-effort
        // semantics — locale persistence must never fault the classifier handler).
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE whatsapp_contacts
               SET "PreferredLocale" = {locale}
             WHERE "Phone" = {phone};
            """, ct);
    }
}
