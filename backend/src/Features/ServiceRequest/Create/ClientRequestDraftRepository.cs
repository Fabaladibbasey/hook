using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.ServiceRequest.Create;

public sealed class ClientRequestDraftRepository(HookDbContext db) : IClientRequestDraftRepository
{
    public Task<ClientRequestDraft?> GetAsync(string phone, CancellationToken ct = default) =>
        db.ClientRequestDrafts.FirstOrDefaultAsync(r => r.Phone == phone, ct);

    public async Task UpsertAsync(ClientRequestDraft draft, CancellationToken ct = default)
    {
        await db.ClientRequestDrafts.UpsertAsync([draft.Phone], draft, (e, d) =>
        {
            e.Step = d.Step;
            e.DraftServiceSlug = d.DraftServiceSlug;
            e.DraftLatitude = d.DraftLatitude;
            e.DraftLongitude = d.DraftLongitude;
            e.DraftFormattedAddress = d.DraftFormattedAddress;
            e.DraftDescription = d.DraftDescription;
            e.UpdatedAt = d.UpdatedAt;
        }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string phone, CancellationToken ct = default)
    {
        if (await db.ClientRequestDrafts.DeleteByKeyAsync([phone], ct))
            await db.SaveChangesAsync(ct);
    }
}
