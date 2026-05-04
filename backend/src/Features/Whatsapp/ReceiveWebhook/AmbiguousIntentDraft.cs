namespace Hook.Features.Whatsapp.ReceiveWebhook;

/// <summary>
/// Short-lived per-phone marker stored when the LLM intent classifier is unsure
/// whether a message is a ServiceRequest or ProviderRegistration. Stores the
/// original message so the next reply ("1" or "2") can replay it into the
/// chosen orchestrator. Drafts older than the TTL are ignored.
/// </summary>
public class AmbiguousIntentDraft
{
    public required string Phone { get; init; }
    public required string OriginalText { get; set; }
    public DateTimeOffset CreatedAt { get; init; }

    public static AmbiguousIntentDraft Start(string phone, string originalText, DateTimeOffset now) => new()
    {
        Phone = phone,
        OriginalText = originalText,
        CreatedAt = now
    };
}
