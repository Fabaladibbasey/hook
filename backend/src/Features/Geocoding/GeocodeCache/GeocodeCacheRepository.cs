using Hook.Features.Geocoding.Models;
using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.Geocoding.GeocodeCache;

public sealed class GeocodeCacheRepository(HookDbContext db, TimeProvider clock) : IGeocodeCache
{
    public async Task<GeocodeResult?> TryGetAsync(string key, CancellationToken ct = default)
    {
        var entry = await db.GeocodeCache.FirstOrDefaultAsync(e => e.Key == key, ct);
        return entry is null
            ? null
            : new GeocodeResult(
                new Location(entry.Latitude, entry.Longitude),
                entry.FormattedAddress,
                entry.Provider,
                FromCache: true);
    }

    public async Task SetAsync(string key, GeocodeResult result, CancellationToken ct = default)
    {
        // First-writer-wins: 23505 against PK is a silent no-op.
        var entry = GeocodeCacheEntry.Capture(key, result, clock.GetUtcNow());
        await db.TryInsertUniqueAsync(entry, ct, GeocodeCacheConstants.PrimaryKeyName);
    }
}
