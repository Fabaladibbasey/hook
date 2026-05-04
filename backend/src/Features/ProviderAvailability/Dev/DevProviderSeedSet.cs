using Hook.Features.Geocoding.Models;

namespace Hook.Features.ProviderAvailability.Dev;

public sealed record DevProviderSpec(
    string Phone,
    IReadOnlyList<string> Services,
    Location Location,
    string FormattedAddress,
    bool ShareContact);

public static class DevProviderSeedSet
{
    private const double LatDegPerKm = 1.0 / 110.574;

    public static IReadOnlyList<DevProviderSpec> All(DevProviderSeedOptions opts)
    {
        var refLat = opts.ReferenceLat;
        var refLng = opts.ReferenceLng;
        var lngDegPerKm = 1.0 / (111.320 * Math.Cos(refLat * Math.PI / 180.0));

        Location Offset(double northKm, double eastKm) =>
            new(refLat + northKm * LatDegPerKm, refLng + eastKm * lngDegPerKm);

        string PhoneAt(int i, string fallback) =>
            i < opts.Phones.Count && !string.IsNullOrWhiteSpace(opts.Phones[i])
                ? opts.Phones[i]
                : fallback;

        var services = new[] { "plumbing", "plumber" };

        return new DevProviderSpec[]
        {
            new(
                Phone: PhoneAt(0, "+2203000001"),
                Services: services,
                Location: Offset(northKm: 1.0, eastKm: 0.0),
                FormattedAddress: "Independence Drive, Banjul, Gambia",
                ShareContact: true),
            new(
                Phone: PhoneAt(1, "+2203000002"),
                Services: services,
                Location: Offset(northKm: 0.0, eastKm: 3.0),
                FormattedAddress: "Kanifing East, Serrekunda, Gambia",
                ShareContact: false),
            new(
                Phone: PhoneAt(2, "+2203000003"),
                Services: services,
                Location: Offset(northKm: -3.2, eastKm: -3.2),
                FormattedAddress: "Bakau New Town, Gambia",
                ShareContact: true)
        };
    }
}
