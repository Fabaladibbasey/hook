using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.ProviderAvailability.Register.AdvanceDraft;

public sealed class ExtendProviderListingHandler(
    IRegistrationDraftRepository drafts,
    IProviderAvailabilityRepository availability,
    IOptions<ProviderAvailabilityOptions> options,
    TimeProvider clock,
    ILogger<ExtendProviderListingHandler> logger)
{
    public async Task Handle(ExtendProviderListingCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        if (!PhoneNumber.TryParse(cmd.Phone, out var phone)) return;
        var now = clock.GetUtcNow();
        var maxServices = options.Value.MaxServicesPerProvider;

        var existing = await availability.GetAsync(cmd.Phone, ct);
        if (existing is null)
        {
            // Listing expired or unlisted while we ran the LLM — silently drop.
            logger.LogDebug("ExtendProviderListing for unlisted {Phone}; dropping", phone.Mask());
            return;
        }

        var newSlugs = cmd.CanonicalSlugs.Except(existing.Services).Distinct().ToList();
        if (newSlugs.Count == 0)
        {
            logger.LogDebug("ExtendProviderListing produced no new slugs for {Phone}", phone.Mask());
            var notice = $"You're already listed for {string.Join(", ", existing.Services)} "
                + $"— extended for {options.Value.ExpiryHours}h. Reply LEAVE to unlist.";
            await bus.PublishAsync(new SendWhatsAppTextCommand(phone, notice));
            return;
        }

        var remaining = maxServices - existing.Services.Count;
        if (remaining <= 0)
        {
            logger.LogDebug("ExtendProviderListing capped for {Phone}; provider already at {Max}", phone.Mask(), maxServices);
            var cap = $"You're already at the {maxServices}-service cap. "
                + "Reply LEAVE to unlist and start over, "
                + $"or stay listed for {string.Join(", ", existing.Services)}.";
            await bus.PublishAsync(new SendWhatsAppTextCommand(phone, cap));
            return;
        }
        var proposed = newSlugs.Take(remaining).ToList();

        var addDraft = RegistrationDraft.Start(cmd.Phone, now);
        addDraft.SetServices(proposed, now);
        addDraft.StepTo(RegistrationStep.ConfirmAddServices, now);
        await drafts.UpsertAsync(addDraft, ct);
        var ackAdd = $"I detected: {string.Join(", ", proposed)}. "
            + "Reply YES to add to your listed services, or EDIT to change.";
        await bus.PublishAsync(new SendWhatsAppTextCommand(phone, ackAdd));
    }
}
