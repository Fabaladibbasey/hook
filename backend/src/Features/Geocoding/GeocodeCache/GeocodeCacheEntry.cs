namespace Hook.Features.Geocoding.GeocodeCache;

public class GeocodeCacheEntry
{
    public required string Key { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public required string FormattedAddress { get; init; }
    public required string Provider { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
}
