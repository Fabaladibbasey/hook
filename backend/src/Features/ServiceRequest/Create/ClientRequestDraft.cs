using Hook.Shared.Domain;

namespace Hook.Features.ServiceRequest.Create;

public enum ClientRequestStep
{
    AwaitingService,
    // Transient: parked while ExtractServicesHandler runs Ollama in the background.
    // Any inbound during this step re-acks "still looking"; falls back to AwaitingService
    // if the resolve hangs past the orchestrator's TTL guard.
    ResolvingService,
    ConfirmService,
    AwaitingLocation,
    ConfirmLocation,
    AwaitingDescription,
    AwaitingPhoneShareConsent,
    Done
}

public class ClientRequestDraft : IAggregateRoot
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
    // Set when entering ResolvingService, cleared on leaving. The TTL revert relies on
    // this rather than UpdatedAt so a follow-up "Touch" while resolving cannot keep the
    // draft trapped indefinitely.
    public DateTimeOffset? ResolveStartedAt { get; private set; }

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
        ResolveStartedAt = step == ClientRequestStep.ResolvingService ? now : null;
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
        ResolveStartedAt = source.ResolveStartedAt;
    }
}
