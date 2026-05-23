using Hook.Features.Ai;
using Hook.Features.Observability;
using Hook.Features.ProviderAvailability.Register.AdvanceDraft;
using Hook.Features.ServiceTaxonomy.ResolveSlug;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.ProviderAvailability.Register.ExtractServices;

public sealed class RegistrationExtractServicesHandler(
    IConversationAi ai,
    SlugResolver slugResolver,
    ILogger<RegistrationExtractServicesHandler> logger)
{
    // [NonTransactional]: AI extraction + slug resolution can take 60-150s.
    // bus.PublishAsync enqueues to the durable outbox so draft mutation +
    // outgoing prompt are committed atomically without pinning the AI worker.
    [NonTransactional]
    public async Task Handle(RegistrationExtractServicesCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        var extracted = await ai.ExtractServicesAsync(cmd.Text, ct);
        var resolved = await slugResolver.ResolveBatchAsync(extracted.Slugs, cmd.Text, ct);
        IReadOnlyList<string> canonical = [.. resolved.Select(r => r.CanonicalSlug)];

        switch (cmd.Mode)
        {
            case RegistrationExtractMode.NewRegistration:
                await bus.PublishAsync(new BeginProviderRegistrationCommand(cmd.Phone, canonical));
                return;
            case RegistrationExtractMode.AddToExisting:
                await bus.PublishAsync(new ExtendProviderListingCommand(cmd.Phone, canonical));
                return;
            case RegistrationExtractMode.AppendToDraft:
                await bus.PublishAsync(new AmendRegistrationDraftCommand(cmd.Phone, canonical));
                return;
            case RegistrationExtractMode.AppendToAddDraft:
                await bus.PublishAsync(new AmendAddServicesDraftCommand(cmd.Phone, canonical));
                return;
            default:
                logger.LogWarning("Unexpected RegistrationExtractMode {Mode} for {Phone}", cmd.Mode, cmd.Phone);
                HookMetrics.AiOutboundDropped.Add(1, new KeyValuePair<string, object?>("stage", "registration-extract-unknown-mode"));
                return;
        }
    }
}
