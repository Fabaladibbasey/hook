using Hook.Features.Tips;
using Hook.Shared.Domain;

namespace Hook.Features.MetaTemplates;

public class WhatsappContact : IAggregateRoot
{
    public string Phone { get; private init; } = string.Empty;
    public DateTimeOffset LastInboundAt { get; private set; }

    // Per-trigger tip cooldown. jsonb dict keyed by TipTrigger ordinal so adding
    // a new trigger does not require a migration. Empty dict = no cooldown set.
    // Writes flow through WhatsappContactRepository raw-SQL jsonb_set, not the
    // tracker; the setter is private to keep that the only write path.
    public Dictionary<TipTrigger, DateTimeOffset> TipCooldowns { get; private set; } = new();

    // Last detected BCP-47 locale from LLM classification. string.Empty for cold
    // accounts; LocaleValidator.Sanitize folds that back to "en" at the call site.
    public string PreferredLocale { get; private set; } = string.Empty;

    public static WhatsappContact Recorded(string phone, DateTimeOffset at) => new()
    {
        Phone = phone,
        LastInboundAt = at
    };
}
