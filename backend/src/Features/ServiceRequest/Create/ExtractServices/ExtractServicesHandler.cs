using Hook.Features.Ai;
using Hook.Features.ServiceRequest.Create.AdvanceDraft;
using Hook.Features.ServiceTaxonomy.ResolveSlug;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.ServiceRequest.Create.ExtractServices;

public sealed class ExtractServicesHandler(
    IConversationAi ai,
    SlugResolver slugResolver)
{
    // [NonTransactional]: AI extraction + slug resolution can take 60-150s.
    // bus.PublishAsync enqueues to the durable outbox so the apply-handler tx
    // + outgoing prompt are committed atomically without pinning the AI worker.
    [NonTransactional]
    public async Task Handle(ExtractServicesCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        var extracted = await ai.ExtractServicesAsync(cmd.Text, ct);
        if (extracted.Slugs.Count == 0)
        {
            await bus.PublishAsync(new ResetClientServiceResolutionCommand(cmd.Phone, cmd.IsSwitch));
            return;
        }

        // Single-slug contract: the client funnel models one service per request, so we
        // pick the first extracted slug and discard the rest. Registration takes the
        // full list because providers may offer multiple services.
        var resolved = await slugResolver.ResolveAsync(extracted.Slugs[0], cmd.Text, ct);
        if (string.IsNullOrEmpty(resolved.CanonicalSlug))
        {
            await bus.PublishAsync(new ResetClientServiceResolutionCommand(cmd.Phone, cmd.IsSwitch));
            return;
        }

        await bus.PublishAsync(
            new ApplyClientServiceResolutionCommand(cmd.Phone, resolved.CanonicalSlug, cmd.IsSwitch));
    }
}
