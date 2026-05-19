using Hook.Shared.Domain;

namespace Hook.Features.ServiceRequest.Create;

public enum ClientRequestStep
{
    AwaitingService,
    ConfirmService,
    AwaitingLocation,
    ConfirmLocation,
    AwaitingDescription,
    AwaitingPhoneShareConsent,
    Done
}

public class ClientRequestDraft : AggregateRoot
{
    public required string Phone { get; init; }
    public ClientRequestStep Step { get; private set; } = ClientRequestStep.AwaitingService;
    public string DraftServiceSlug { get; private set; } = string.Empty;
    public double? DraftLatitude { get; private set; }
    public double? DraftLongitude { get; private set; }
    public string DraftFormattedAddress { get; private set; } = string.Empty;
    public string DraftDescription { get; private set; } = string.Empty;
    public bool? DraftSharePhoneConsent { get; private set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static ClientRequestDraft Start(string phone, DateTimeOffset now) => new()
    {
        Phone = phone,
        StartedAt = now,
        UpdatedAt = now
    };

    public void StepTo(ClientRequestStep step, DateTimeOffset now)
    {
        Step = step;
        UpdatedAt = now;
    }

    public void SwitchSlug(string slug, DateTimeOffset now)
    {
        DraftServiceSlug = slug;
        UpdatedAt = now;
    }

    public void CaptureLocation(double lat, double lon, string formattedAddress, DateTimeOffset now)
    {
        DraftLatitude = lat;
        DraftLongitude = lon;
        DraftFormattedAddress = formattedAddress;
        UpdatedAt = now;
    }

    public void CaptureDescription(string description, DateTimeOffset now)
    {
        DraftDescription = description;
        UpdatedAt = now;
    }

    public void SetPhoneShareConsent(bool consent, DateTimeOffset now)
    {
        DraftSharePhoneConsent = consent;
        UpdatedAt = now;
    }

    public void Touch(DateTimeOffset now) => UpdatedAt = now;

    internal void ReplaceStateFrom(ClientRequestDraft source)
    {
        Step = source.Step;
        DraftServiceSlug = source.DraftServiceSlug;
        DraftLatitude = source.DraftLatitude;
        DraftLongitude = source.DraftLongitude;
        DraftFormattedAddress = source.DraftFormattedAddress;
        DraftDescription = source.DraftDescription;
        DraftSharePhoneConsent = source.DraftSharePhoneConsent;
        UpdatedAt = source.UpdatedAt;
    }
}
