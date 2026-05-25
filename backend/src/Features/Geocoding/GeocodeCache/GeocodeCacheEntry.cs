using Hook.Features.Geocoding.Models;
using Hook.Shared.Domain;

namespace Hook.Features.Geocoding.GeocodeCache;

public class GeocodeCacheEntry : IAggregateRoot
{
    public string Key { get; private init; } = string.Empty;
    public double Latitude { get; private init; }
    public double Longitude { get; private init; }
    public string FormattedAddress { get; private init; } = string.Empty;
    public string Provider { get; private init; } = string.Empty;
    public DateTimeOffset FetchedAt { get; private init; }

    public static GeocodeCacheEntry Capture(string key, GeocodeResult result, DateTimeOffset fetchedAt) => new()
    {
        Key = key,
        Latitude = result.Location.Latitude,
        Longitude = result.Location.Longitude,
        FormattedAddress = result.FormattedAddress,
        Provider = result.Provider,
        FetchedAt = fetchedAt
    };
}
