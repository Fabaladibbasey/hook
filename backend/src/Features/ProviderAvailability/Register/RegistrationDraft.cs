namespace Hook.Features.ProviderAvailability.Register;

public enum RegistrationStep
{
    AwaitingServices,
    ConfirmServices,
    AwaitingLocation,
    ConfirmLocation,
    AwaitingConsent,
    Done
}

public class RegistrationDraft
{
    public required string Phone { get; init; }
    public RegistrationStep Step { get; set; } = RegistrationStep.AwaitingServices;
    public List<string> DraftServices { get; set; } = [];
    public double? DraftLatitude { get; set; }
    public double? DraftLongitude { get; set; }
    public string DraftFormattedAddress { get; set; } = string.Empty;
    public bool? DraftShareContact { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static RegistrationDraft Start(string phone, DateTimeOffset now) => new()
    {
        Phone = phone,
        StartedAt = now,
        UpdatedAt = now
    };
}
