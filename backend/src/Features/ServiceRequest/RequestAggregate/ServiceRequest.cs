using NetTopologySuite.Geometries;
using Location = Hook.Features.Geocoding.Models.Location;

namespace Hook.Features.ServiceRequest.RequestAggregate;

public class ServiceRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string ClientPhone { get; init; }
    public required string ServiceSlug { get; init; }
    public required Point Location { get; init; }
    public string FormattedAddress { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public ServiceRequestStatus Status { get; private set; } = ServiceRequestStatus.Open;

    public List<string> ShownProviderPhones { get; private set; } = new();
    public double CurrentRadiusKm { get; set; }
    public bool AutoExpandedOnce { get; set; }
    public bool SharePhoneNumber { get; private set; }

    public static ServiceRequest Create(
        string clientPhone,
        string serviceSlug,
        Location location,
        string formattedAddress,
        string description,
        double initialRadiusKm,
        DateTimeOffset now,
        bool sharePhoneNumber) => new()
        {
            Id = Guid.NewGuid(),
            ClientPhone = clientPhone,
            ServiceSlug = serviceSlug,
            Location = location.ToPoint(),
            FormattedAddress = formattedAddress,
            Description = description,
            CreatedAt = now,
            Status = ServiceRequestStatus.Open,
            CurrentRadiusKm = initialRadiusKm,
            SharePhoneNumber = sharePhoneNumber
        };

    public void RecordShown(IEnumerable<string> providerPhones)
    {
        foreach (var phone in providerPhones)
        {
            if (!ShownProviderPhones.Contains(phone))
                ShownProviderPhones.Add(phone);
        }
    }

    public void MarkMatched() => Status = ServiceRequestStatus.Matched;
    public void Close() => Status = ServiceRequestStatus.Closed;
}
