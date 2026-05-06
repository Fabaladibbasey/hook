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

public class ClientRequestDraft
{
    public required string Phone { get; init; }
    public ClientRequestStep Step { get; set; } = ClientRequestStep.AwaitingService;
    public string DraftServiceSlug { get; set; } = string.Empty;
    public double? DraftLatitude { get; set; }
    public double? DraftLongitude { get; set; }
    public string DraftFormattedAddress { get; set; } = string.Empty;
    public string DraftDescription { get; set; } = string.Empty;
    public bool? DraftSharePhoneConsent { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static ClientRequestDraft Start(string phone, DateTimeOffset now) => new()
    {
        Phone = phone,
        StartedAt = now,
        UpdatedAt = now
    };
}
