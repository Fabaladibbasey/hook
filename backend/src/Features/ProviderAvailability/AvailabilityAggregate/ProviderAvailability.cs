using NetTopologySuite.Geometries;
using Location = Hook.Features.Geocoding.Models.Location;

namespace Hook.Features.ProviderAvailability.AvailabilityAggregate;

public class ProviderAvailability
{
    public required string Phone { get; init; }
    public List<string> Services { get; private set; } = new();
    public Point Location { get; private set; } = default!;
    public string FormattedAddress { get; private set; } = string.Empty;
    public bool ShareContact { get; private set; }
    public DateTimeOffset LastActiveAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public static ProviderAvailability Register(
        string phone,
        IEnumerable<string> services,
        Location location,
        string formattedAddress,
        bool shareContact,
        TimeSpan ttl,
        DateTimeOffset now)
    {
        return new ProviderAvailability
        {
            Phone = phone,
            Services = services.Distinct().ToList(),
            Location = location.ToPoint(),
            FormattedAddress = formattedAddress,
            ShareContact = shareContact,
            LastActiveAt = now,
            ExpiresAt = now + ttl
        };
    }

    public void Heartbeat(TimeSpan ttl, DateTimeOffset now)
    {
        LastActiveAt = now;
        ExpiresAt = now + ttl;
    }

    public void UpdateServices(IEnumerable<string> services)
    {
        Services = services.Distinct().ToList();
    }

    public void UpdateLocation(Location location, string formattedAddress)
    {
        Location = location.ToPoint();
        FormattedAddress = formattedAddress;
    }

    public void UpdateConsent(bool shareContact)
    {
        ShareContact = shareContact;
    }

    public bool IsActive(DateTimeOffset now) => ExpiresAt > now;
}
