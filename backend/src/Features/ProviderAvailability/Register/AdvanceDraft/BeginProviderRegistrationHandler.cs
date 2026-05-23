using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.ProviderAvailability.Register.AdvanceDraft;

public sealed class BeginProviderRegistrationHandler(
    IRegistrationDraftRepository drafts,
    IOptions<ProviderAvailabilityOptions> options,
    TimeProvider clock,
    ILogger<BeginProviderRegistrationHandler> logger)
{
    public async Task Handle(BeginProviderRegistrationCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        if (!PhoneNumber.TryParse(cmd.Phone, out var phone)) return;
        var draft = await drafts.GetAsync(cmd.Phone, ct);
        if (draft is null) return;
        var now = clock.GetUtcNow();
        var maxServices = options.Value.MaxServicesPerProvider;

        if (cmd.CanonicalSlugs.Count == 0)
        {
            logger.LogDebug("No canonical slugs for {Phone}; resetting to AwaitingServices.", phone.Mask());
            draft.StepTo(RegistrationStep.AwaitingServices, now);
            await drafts.UpsertAsync(draft, ct);
            var prompt = "Tell me what services you offer (e.g. plumbing, carpentry, computer repair) "
                + "— or reply REQUEST if you need a service instead.";
            await bus.PublishAsync(new SendWhatsAppTextCommand(phone, prompt));
            return;
        }

        var distinctSlugs = cmd.CanonicalSlugs.Distinct().ToList();
        var capped = distinctSlugs.Take(maxServices).ToList();
        draft.SetServices(capped, now);
        draft.StepTo(RegistrationStep.ConfirmServices, now);
        await drafts.UpsertAsync(draft, ct);

        var ack = distinctSlugs.Count > maxServices
            ? $"Max {maxServices} services per provider. Keeping: {string.Join(", ", capped)}. Reply YES or EDIT."
            : $"I detected: {string.Join(", ", capped)}. Reply YES to confirm or EDIT to change.";
        await bus.PublishAsync(new SendWhatsAppTextCommand(phone, ack));
    }
}
