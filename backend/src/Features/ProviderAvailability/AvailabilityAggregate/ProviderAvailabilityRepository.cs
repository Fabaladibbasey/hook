using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.ProviderAvailability.AvailabilityAggregate;

public sealed class ProviderAvailabilityRepository(HookDbContext db) : IProviderAvailabilityRepository
{
    public Task<ProviderAvailability?> GetAsync(string phone, CancellationToken ct = default) =>
        db.ProviderAvailabilities.FirstOrDefaultAsync(p => p.Phone == phone, ct);

    public async Task AddAsync(ProviderAvailability availability, CancellationToken ct = default) =>
        await db.ProviderAvailabilities.AddAsync(availability, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
