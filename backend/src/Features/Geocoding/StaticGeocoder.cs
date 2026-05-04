using Hook.Features.Geocoding.Models;
using Microsoft.Extensions.Options;

namespace Hook.Features.Geocoding;

public sealed class StaticGeocoder(IOptions<DevGeocodingOptions> options) : IGeocoder
{
    private readonly Location _default = new(options.Value.DefaultLat, options.Value.DefaultLng);

    public Task<GeocodeResult?> GeocodeAsync(string address, CancellationToken ct = default)
    {
        var formatted = string.IsNullOrWhiteSpace(address) ? "Unknown" : address.Trim();
        var result = new GeocodeResult(_default, formatted, "static-dev", FromCache: false);
        return Task.FromResult<GeocodeResult?>(result);
    }
}
