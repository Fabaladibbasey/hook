using Hook.Features.Ai;
using Hook.Features.ProviderAvailability.Register.AdvanceDraft;
using Hook.Features.ServiceTaxonomy.ResolveSlug;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.ProviderAvailability.Register.ExtractServices;

public sealed class RegistrationExtractServicesHandler(
    IConversationAi ai,
    SlugResolver slugResolver)
{
    // [NonTransactional]: AI extraction + slug resolution can take 60-150s.
    // bus.InvokeAsync re-enters a transactional handler so draft mutation +
    // outgoing prompt are committed atomically with the outbox envelope.
    [NonTransactional]
    public async Task Handle(RegistrationExtractServicesRequested evt, IMessageBus bus, CancellationToken ct)
    {
        var extracted = await ai.ExtractServicesAsync(evt.Text, ct);
        var resolved = await slugResolver.ResolveBatchAsync(extracted.Slugs, evt.Text, ct);
        IReadOnlyList<string> canonical = [.. resolved.Select(r => r.CanonicalSlug)];

        await bus.InvokeAsync(new AdvanceRegistrationDraft(evt.Phone, canonical, evt.Mode), ct);
    }
}
