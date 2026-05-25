using Hook.Shared.Domain;
using NetTopologySuite.Geometries;
using Location = Hook.Features.Geocoding.Models.Location;

namespace Hook.Features.ServiceRequest.RequestAggregate;

public class ServiceRequest : AggregateRoot
{
    public Guid Id { get; private init; }
    public string ClientPhone { get; private init; } = string.Empty;
    public string ServiceSlug { get; private init; } = string.Empty;
    public Point Location { get; private init; } = default!;
    public string FormattedAddress { get; private init; } = string.Empty;
    public string Description { get; private init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private init; }
    public ServiceRequestStatus Status { get; private set; } = ServiceRequestStatus.Open;

    public List<string> ShownProviderPhones { get; private set; } = [];
    public double CurrentRadiusKm { get; private set; }
    public bool AutoExpandedOnce { get; private set; }
    public bool SharePhoneNumber { get; private set; }

    public static ServiceRequest Create(
        string clientPhone,
        string serviceSlug,
        Location location,
        string formattedAddress,
        string description,
        double initialRadiusKm,
        DateTimeOffset now,
        bool sharePhoneNumber)
    {
        var request = new ServiceRequest
        {
            Id = Guid.CreateVersion7(),
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
        request.RaiseDomainEvent(new ServiceRequestCreatedEvent(request.Id));
        return request;
    }

    public void RecordShown(IEnumerable<string> providerPhones)
    {
        var existing = ShownProviderPhones.ToHashSet(StringComparer.Ordinal);
        foreach (var phone in providerPhones)
            if (existing.Add(phone)) ShownProviderPhones.Add(phone);
    }

    public bool IsRadiusAtMax(double maxKm) => CurrentRadiusKm >= maxKm;

    public double NextRadiusAfterExpansion(double factor, double maxKm) =>
        ClampToMax(factor, maxKm);

    public bool ExpandRadius(double factor, double maxKm)
    {
        if (IsRadiusAtMax(maxKm)) return false;
        CurrentRadiusKm = ClampToMax(factor, maxKm);
        return true;
    }

    public bool TryAutoExpandOnce(double factor, double maxKm)
    {
        if (AutoExpandedOnce || IsRadiusAtMax(maxKm)) return false;
        CurrentRadiusKm = ClampToMax(factor, maxKm);
        AutoExpandedOnce = true;
        return true;
    }

    private double ClampToMax(double factor, double maxKm) =>
        Math.Min(CurrentRadiusKm * factor, maxKm);

    public void MarkMatched()
    {
        if (Status == ServiceRequestStatus.Matched) return;
        if (Status == ServiceRequestStatus.Closed)
            throw new InvalidOperationException(
                $"ServiceRequest {Id} is Closed; cannot re-open as Matched.");
        Status = ServiceRequestStatus.Matched;
    }

    public void Close()
    {
        if (Status == ServiceRequestStatus.Closed) return;
        Status = ServiceRequestStatus.Closed;
    }
}
