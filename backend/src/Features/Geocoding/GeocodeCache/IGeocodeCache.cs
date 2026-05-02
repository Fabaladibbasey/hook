using Hook.Features.Geocoding.Models;

namespace Hook.Features.Geocoding.GeocodeCache;

public interface IGeocodeCache
{
    Task<GeocodeResult?> TryGetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, GeocodeResult result, CancellationToken ct = default);
}
