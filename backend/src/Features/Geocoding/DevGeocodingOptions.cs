namespace Hook.Features.Geocoding;

public class DevGeocodingOptions
{
    public const string SectionName = "Dev:Geocoding";

    public bool Enabled { get; init; }

    // Coordinates returned by the StaticGeocoder dev stub for any input address.
    // Banjul, The Gambia — matches the deployment region.
    public double DefaultLat { get; init; } = 13.4549;
    public double DefaultLng { get; init; } = -16.5790;
}
