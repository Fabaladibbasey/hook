using System.ComponentModel.DataAnnotations;

namespace Hook.Features.Geocoding;

public class GoogleGeocodingOptions
{
    public const string SectionName = "GoogleGeocoding";

    [Required]
    public string ApiKey { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = "https://maps.googleapis.com";
}
