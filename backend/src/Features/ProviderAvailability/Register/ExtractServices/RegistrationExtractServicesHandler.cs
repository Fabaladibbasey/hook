using Hook.Features.Ai;
using Hook.Features.ProviderAvailability.Register.AdvanceDraft;
using Hook.Features.ServiceTaxonomy.ResolveSlug;
using Hook.Features.Whatsapp.Phone;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.ProviderAvailability.Register.ExtractServices;

public sealed class RegistrationExtractServicesHandler(
    IConversationAi ai,
    SlugResolver slugResolver,
    ILogger<RegistrationExtractServicesHandler> logger)
{
    // [NonTransactional]: AI extraction + slug resolution can take 60-150s.
    // bus.InvokeAsync re-enters a transactional handler so draft mutation +
    // outgoing prompt are committed atomically with the outbox envelope.
    [NonTransactional]
    public async Task Handle(RegistrationExtractServicesRequested evt, IMessageBus bus, CancellationToken ct)
    {
        IReadOnlyList<string> canonical;
        try
        {
            var extracted = await ai.ExtractServicesAsync(evt.Text, ct);
            if (extracted.Slugs.Count == 0)
            {
                canonical = [];
            }
            else
            {
                var resolved = new List<string>(extracted.Slugs.Count);
                foreach (var slug in extracted.Slugs)
                {
                    var r = await slugResolver.ResolveAsync(slug, evt.Text, ct);
                    resolved.Add(r.CanonicalSlug);
                }
                canonical = resolved;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Registration extract failed for {Phone}; treating as no-slug", MaskPhone(evt.Phone));
            canonical = [];
        }

        await bus.InvokeAsync(new AdvanceRegistrationDraft(evt.Phone, canonical, evt.Mode), ct);
    }

    private static string MaskPhone(string raw) =>
        PhoneNumber.TryParse(raw, out var p) ? p.Mask() : "<unparseable>";
}
