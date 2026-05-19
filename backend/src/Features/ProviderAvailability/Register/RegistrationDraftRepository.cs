using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.ProviderAvailability.Register;

public sealed class RegistrationDraftRepository(HookDbContext db) : IRegistrationDraftRepository
{
    public Task<RegistrationDraft?> GetAsync(string phone, CancellationToken ct = default) =>
        db.RegistrationDrafts.FirstOrDefaultAsync(r => r.Phone == phone, ct);

    public Task UpsertAsync(RegistrationDraft draft, CancellationToken ct = default) =>
        db.RegistrationDrafts.UpsertAsync([draft.Phone], draft, (e, d) => e.ReplaceStateFrom(d), ct);

    public Task DeleteAsync(string phone, CancellationToken ct = default) =>
        db.RegistrationDrafts.DeleteByKeyAsync([phone], ct);
}
