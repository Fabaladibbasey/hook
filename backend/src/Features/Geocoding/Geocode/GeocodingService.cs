using Hook.Features.Geocoding.GeocodeCache;
using Hook.Features.Geocoding.Models;
using Hook.Features.Observability;

namespace Hook.Features.Geocoding.Geocode;

public sealed class GeocodingService(
    IGeocoder geocoder,
    IGeocodeCache cache,
    ILogger<GeocodingService> logger)
{
    public async Task<GeocodeResult?> GeocodeAsync(string rawAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawAddress)) return null;

        var key = NormalizeKey(rawAddress);
        var cached = await cache.TryGetAsync(key, ct);
        if (cached is not null)
        {
            HookMetrics.GeocodeCacheHits.Add(1);
            return cached;
        }

        HookMetrics.GeocodeApiCalls.Add(1);
        var fresh = await geocoder.GeocodeAsync(rawAddress, ct);
        if (fresh is null)
        {
            logger.LogInformation("Geocoder returned no result for '{Address}'", rawAddress);
            return null;
        }

        await cache.SetAsync(key, fresh, ct);
        return fresh;
    }

    public static string NormalizeKey(string raw) => raw.Trim().ToLowerInvariant();
}
