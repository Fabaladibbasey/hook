using Hook.Shared.Domain;

namespace Hook.Features.ProviderAvailability.Register;

public enum RegistrationStep
{
    AwaitingServices,
    ConfirmServices,
    AwaitingLocation,
    ConfirmLocation,
    AwaitingConsent,
    ConfirmAddServices,
    Done
}

public class RegistrationDraft : IAggregateRoot
{
    public required string Phone { get; init; }
    public RegistrationStep Step { get; private set; } = RegistrationStep.AwaitingServices;
    public List<string> DraftServices { get; private set; } = [];
    public double? DraftLatitude { get; private set; }
    public double? DraftLongitude { get; private set; }
    public string DraftFormattedAddress { get; private set; } = string.Empty;
    public bool? DraftShareContact { get; private set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static RegistrationDraft Start(string phone, DateTimeOffset now) => new()
    {
        Phone = phone,
        StartedAt = now,
        UpdatedAt = now
    };

    public void StepTo(RegistrationStep step, DateTimeOffset now)
    {
        Step = step;
        UpdatedAt = now;
    }

    public void SetServices(IEnumerable<string> services, DateTimeOffset now)
    {
        DraftServices = [.. services];
        UpdatedAt = now;
    }

    public void CaptureLocation(double lat, double lon, string formattedAddress, DateTimeOffset now)
    {
        DraftLatitude = lat;
        DraftLongitude = lon;
        DraftFormattedAddress = formattedAddress;
        UpdatedAt = now;
    }

    public void SetShareContact(bool consent, DateTimeOffset now)
    {
        DraftShareContact = consent;
        UpdatedAt = now;
    }

    public void Touch(DateTimeOffset now) => UpdatedAt = now;

    internal void ReplaceStateFrom(RegistrationDraft source)
    {
        Step = source.Step;
        DraftServices = [.. source.DraftServices];
        DraftLatitude = source.DraftLatitude;
        DraftLongitude = source.DraftLongitude;
        DraftFormattedAddress = source.DraftFormattedAddress;
        DraftShareContact = source.DraftShareContact;
        UpdatedAt = source.UpdatedAt;
    }
}
