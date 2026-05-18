using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.ProviderAvailability.AvailabilityAggregate;

public sealed class ProviderAvailabilityRepository(HookDbContext db) : IProviderAvailabilityRepository
{
    public Task<ProviderAvailability?> GetAsync(string phone, CancellationToken ct = default) =>
        db.ProviderAvailabilities.FirstOrDefaultAsync(p => p.Phone == phone, ct);

    public async Task AddAsync(ProviderAvailability availability, CancellationToken ct = default) =>
        await db.ProviderAvailabilities.AddAsync(availability, ct);

    public async Task RemoveAsync(string phone, CancellationToken ct = default)
    {
        var row = await db.ProviderAvailabilities.FirstOrDefaultAsync(p => p.Phone == phone, ct);
        if (row is not null) db.ProviderAvailabilities.Remove(row);
    }
}
