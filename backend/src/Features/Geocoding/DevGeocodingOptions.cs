namespace Hook.Features.Geocoding;

public class DevGeocodingOptions
{
    public const string SectionName = "Dev:Geocoding";

    public bool Enabled { get; init; }

    // Coordinates returned by the StaticGeocoder dev stub for any input address.
    // Defaults preserve historical SF behaviour for backwards-compat with integration tests.
    public double DefaultLat { get; init; } = 37.7749;
    public double DefaultLng { get; init; } = -122.4194;
}
