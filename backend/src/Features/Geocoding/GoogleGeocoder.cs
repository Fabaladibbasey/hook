using System.Text.Json;
using Hook.Features.Geocoding.Models;
using Microsoft.Extensions.Options;

namespace Hook.Features.Geocoding;

public sealed class GoogleGeocoder(
    HttpClient httpClient,
    IOptions<GoogleGeocodingOptions> options,
    ILogger<GoogleGeocoder> logger) : IGeocoder
{
    public const string HttpClientName = "geocoding.google";
    public const string ProviderName = "google";

    public async Task<GeocodeResult?> GeocodeAsync(string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;

        var url = $"/maps/api/geocode/json?address={Uri.EscapeDataString(address)}&key={options.Value.ApiKey}";
        using var response = await httpClient.GetAsync(url, ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Google geocoding HTTP {Status} for '{Address}'", (int)response.StatusCode, address);
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var status = root.TryGetProperty("status", out var s) ? s.GetString() : "UNKNOWN";
        if (!string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Google geocoding status={Status} for '{Address}'", status, address);
            return null;
        }

        if (!root.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return null;

        var first = results[0];
        var formatted = first.GetProperty("formatted_address").GetString() ?? address;
        var loc = first.GetProperty("geometry").GetProperty("location");
        var lat = loc.GetProperty("lat").GetDouble();
        var lng = loc.GetProperty("lng").GetDouble();

        return new GeocodeResult(new Location(lat, lng), formatted, ProviderName, FromCache: false);
    }
}
