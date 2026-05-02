namespace Hook.Features.Geocoding.Models;

public sealed record GeocodeResult(Location Location, string FormattedAddress, string Provider, bool FromCache);
