using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

public sealed class AmbiguousIntentDraftRepository(HookDbContext db) : IAmbiguousIntentDraftRepository
{
    public Task<AmbiguousIntentDraft?> GetAsync(string phone, CancellationToken ct = default) =>
        db.AmbiguousIntentDrafts.FirstOrDefaultAsync(d => d.Phone == phone, ct);

    public async Task UpsertAsync(AmbiguousIntentDraft draft, CancellationToken ct = default)
    {
        await db.AmbiguousIntentDrafts.UpsertAsync([draft.Phone], draft, (e, d) =>
        {
            e.OriginalText = d.OriginalText;
        }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string phone, CancellationToken ct = default)
    {
        if (await db.AmbiguousIntentDrafts.DeleteByKeyAsync([phone], ct))
            await db.SaveChangesAsync(ct);
    }
}
