using Hook.Features.Ai;
using Hook.Features.ServiceRequest.Create.AdvanceDraft;
using Hook.Features.ServiceTaxonomy.ResolveSlug;
using Hook.Features.Whatsapp.Phone;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.ServiceRequest.Create.ExtractServices;

public sealed class ExtractServicesHandler(
    IConversationAi ai,
    SlugResolver slugResolver,
    ILogger<ExtractServicesHandler> logger)
{
    // [NonTransactional]: AI extraction + slug resolution can take 60-150s.
    // bus.InvokeAsync re-enters a transactional handler so the draft mutation
    // + outgoing prompt are committed atomically with the outbox envelope.
    [NonTransactional]
    public async Task Handle(ExtractServicesRequested evt, IMessageBus bus, CancellationToken ct)
    {
        string canonical;
        try
        {
            var extracted = await ai.ExtractServicesAsync(evt.Text, ct);
            if (extracted.Slugs.Count == 0)
            {
                canonical = string.Empty;
            }
            else
            {
                // Single-slug contract: the client funnel models one service per request, so we
                // pick the first extracted slug and discard the rest. Registration takes the
                // full list because providers may offer multiple services.
                var resolved = await slugResolver.ResolveAsync(extracted.Slugs[0], evt.Text, ct);
                canonical = resolved.CanonicalSlug;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Service extraction failed for {Phone}; treating as no-slug", MaskPhone(evt.Phone));
            canonical = string.Empty;
        }

        await bus.InvokeAsync(new AdvanceClientRequestDraft(evt.Phone, canonical, evt.IsSwitch), ct);
    }

    private static string MaskPhone(string raw) =>
        PhoneNumber.TryParse(raw, out var p) ? p.Mask() : "<unparseable>";
}
