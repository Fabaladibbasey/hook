using Hook.Features.Geocoding.Models;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.Geocoding.GeocodeCache;

public sealed class GeocodeCacheRepository(HookDbContext db) : IGeocodeCache
{
    public async Task<GeocodeResult?> TryGetAsync(string key, CancellationToken ct = default)
    {
        var entry = await db.GeocodeCache.FirstOrDefaultAsync(e => e.Key == key, ct);
        return entry is null
            ? null
            : new GeocodeResult(new Location(entry.Latitude, entry.Longitude), entry.FormattedAddress, entry.Provider, FromCache: true);
    }

    public async Task SetAsync(string key, GeocodeResult result, CancellationToken ct = default)
    {
        var existing = await db.GeocodeCache.FindAsync([key], ct);
        if (existing is not null) return;

        await db.GeocodeCache.AddAsync(new GeocodeCacheEntry
        {
            Key = key,
            Latitude = result.Location.Latitude,
            Longitude = result.Location.Longitude,
            FormattedAddress = result.FormattedAddress,
            Provider = result.Provider,
            FetchedAt = DateTimeOffset.UtcNow
        }, ct);
        await db.SaveChangesAsync(ct);
    }
}
