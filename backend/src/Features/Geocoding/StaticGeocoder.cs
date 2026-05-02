using Hook.Features.Geocoding.Models;

namespace Hook.Features.Geocoding;

public sealed class StaticGeocoder : IGeocoder
{
    private static readonly Location Default = new(37.7749, -122.4194);

    public Task<GeocodeResult?> GeocodeAsync(string address, CancellationToken ct = default)
    {
        var formatted = string.IsNullOrWhiteSpace(address) ? "Unknown" : address.Trim();
        var result = new GeocodeResult(Default, formatted, "static-dev", FromCache: false);
        return Task.FromResult<GeocodeResult?>(result);
    }
}
