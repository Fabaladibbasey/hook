using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.ProviderAvailability.Register.AdvanceDraft;

public sealed class AmendRegistrationDraftHandler(
    IRegistrationDraftRepository drafts,
    IOptions<ProviderAvailabilityOptions> options,
    TimeProvider clock,
    ILogger<AmendRegistrationDraftHandler> logger)
{
    public async Task Handle(AmendRegistrationDraftCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        if (!PhoneNumber.TryParse(cmd.Phone, out var phone)) return;

        var draft = await drafts.GetAsync(cmd.Phone, ct);
        if (draft is not { Step: RegistrationStep.ConfirmServices })
        {
            logger.LogDebug("Stale AmendRegistrationDraft for {Phone}; draft now at {Step}", phone.Mask(), draft?.Step);
            return;
        }

        var now = clock.GetUtcNow();
        var maxServices = options.Value.MaxServicesPerProvider;

        var merged = draft.DraftServices.Concat(cmd.CanonicalSlugs).Distinct().Take(maxServices).ToList();
        if (merged.Count == draft.DraftServices.Count)
        {
            await bus.PublishAsync(new SendWhatsAppTextCommand(phone,
                $"Already listed: {string.Join(", ", draft.DraftServices)}. Reply YES to confirm or EDIT to change."));
            return;
        }

        draft.SetServices(merged, now);
        await drafts.UpsertAsync(draft, ct);
        await bus.PublishAsync(new SendWhatsAppTextCommand(phone,
            $"Updated: {string.Join(", ", merged)}. Reply YES to confirm or EDIT to change."));
    }
}
