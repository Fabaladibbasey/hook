using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.ProviderAvailability.Register.AdvanceDraft;

public sealed class AmendAddServicesDraftHandler(
    IRegistrationDraftRepository drafts,
    IProviderAvailabilityRepository availability,
    IOptions<ProviderAvailabilityOptions> options,
    TimeProvider clock,
    ILogger<AmendAddServicesDraftHandler> logger)
{
    public async Task Handle(AmendAddServicesDraftCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        if (!PhoneNumber.TryParse(cmd.Phone, out var phone)) return;
        var now = clock.GetUtcNow();
        var maxServices = options.Value.MaxServicesPerProvider;

        var existing = await availability.GetAsync(cmd.Phone, ct);
        if (existing is null)
        {
            logger.LogDebug("AmendAddServicesDraft for unlisted {Phone}; dropping", phone.Mask());
            return;
        }

        var draft = await drafts.GetAsync(cmd.Phone, ct);
        if (draft is not { Step: RegistrationStep.ConfirmAddServices })
        {
            logger.LogDebug("Stale AmendAddServicesDraft for {Phone}; draft now at {Step}", phone.Mask(), draft?.Step);
            return;
        }

        var remainingCap = maxServices - existing.Services.Count;
        var merged = draft.DraftServices
            .Concat(cmd.CanonicalSlugs.Where(s => !existing.Services.Contains(s)))
            .Distinct()
            .Take(remainingCap)
            .ToList();
        if (merged.Count == draft.DraftServices.Count)
        {
            await bus.PublishAsync(new SendWhatsAppTextCommand(phone,
                $"Pending add: {string.Join(", ", draft.DraftServices)}. Reply YES to add, or EDIT to change."));
            return;
        }

        draft.SetServices(merged, now);
        await drafts.UpsertAsync(draft, ct);
        await bus.PublishAsync(new SendWhatsAppTextCommand(phone,
            $"Updated: {string.Join(", ", merged)}. Reply YES to add, or EDIT to change."));
    }
}
