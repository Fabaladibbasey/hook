using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

public sealed class AmbiguousIntentDraftRepository(HookDbContext db) : IAmbiguousIntentDraftRepository
{
    public Task<AmbiguousIntentDraft?> GetAsync(string phone, CancellationToken ct = default) =>
        db.AmbiguousIntentDrafts.FirstOrDefaultAsync(d => d.Phone == phone, ct);

    public Task UpsertAsync(AmbiguousIntentDraft draft, CancellationToken ct = default) =>
        db.AmbiguousIntentDrafts.UpsertAsync([draft.Phone], draft, (e, d) => e.Refresh(d.OriginalText), ct);

    public Task DeleteAsync(string phone, CancellationToken ct = default) =>
        db.AmbiguousIntentDrafts.DeleteByKeyAsync([phone], ct);
}
