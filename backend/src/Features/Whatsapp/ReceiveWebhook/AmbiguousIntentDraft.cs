using Hook.Shared.Domain;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

/// <summary>
/// Short-lived per-phone marker stored when the LLM intent classifier is unsure
/// whether a message is a ServiceRequest or ProviderRegistration. Stores the
/// original message so the next reply ("1" or "2") can replay it into the
/// chosen orchestrator. Drafts older than the TTL are ignored.
/// </summary>
public class AmbiguousIntentDraft : IAggregateRoot
{
    public string Phone { get; private init; } = string.Empty;
    public string OriginalText { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private init; }

    public static AmbiguousIntentDraft Start(string phone, string originalText, DateTimeOffset now) => new()
    {
        Phone = phone,
        OriginalText = originalText,
        CreatedAt = now
    };

    public void Refresh(string originalText) => OriginalText = originalText;
}
