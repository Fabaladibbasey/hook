using Hook.Features.Geocoding.Models;

namespace Hook.Features.Geocoding;

public interface IGeocoder
{
    Task<GeocodeResult?> GeocodeAsync(string address, CancellationToken ct = default);
}
