using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Register.ExtractServices;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.ProviderAvailability.Register.AdvanceDraft;

public sealed class AdvanceRegistrationDraftHandler(
    IRegistrationDraftRepository drafts,
    IProviderAvailabilityRepository availability,
    IOptions<ProviderAvailabilityOptions> options,
    TimeProvider clock,
    ILogger<AdvanceRegistrationDraftHandler> logger)
{
    public async Task Handle(AdvanceRegistrationDraft evt, IMessageBus bus, CancellationToken ct)
    {
        if (!PhoneNumber.TryParse(evt.Phone, out var phone)) return;
        var now = clock.GetUtcNow();
        var maxServices = options.Value.MaxServicesPerProvider;

        switch (evt.Mode)
        {
            case RegistrationExtractMode.NewRegistration:
                await HandleNewRegistrationAsync(evt, phone, now, maxServices, bus, ct);
                return;
            case RegistrationExtractMode.AddToExisting:
                await HandleAddToExistingAsync(evt, phone, now, maxServices, bus, ct);
                return;
            case RegistrationExtractMode.AppendToDraft:
                await HandleAppendToDraftAsync(evt, phone, now, maxServices, bus, ct);
                return;
            case RegistrationExtractMode.AppendToAddDraft:
                await HandleAppendToAddDraftAsync(evt, phone, now, maxServices, bus, ct);
                return;
            default:
                logger.LogWarning("Unexpected RegistrationExtractMode {Mode} for {Phone}", evt.Mode, phone.Mask());
                return;
        }
    }

    private async Task HandleNewRegistrationAsync(
        AdvanceRegistrationDraft evt,
        PhoneNumber phone,
        DateTimeOffset now,
        int maxServices,
        IMessageBus bus,
        CancellationToken ct)
    {
        var draft = await drafts.GetAsync(evt.Phone, ct);
        if (draft is null) return;

        if (evt.CanonicalSlugs.Count == 0)
        {
            draft.StepTo(RegistrationStep.AwaitingServices, now);
            await drafts.UpsertAsync(draft, ct);
            var prompt = "Tell me what services you offer (e.g. plumbing, carpentry, computer repair) "
                + "— or reply REQUEST if you need a service instead.";
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, prompt));
            return;
        }

        var capped = evt.CanonicalSlugs.Distinct().Take(maxServices).ToList();
        draft.SetServices(capped, now);
        draft.StepTo(RegistrationStep.ConfirmServices, now);
        await drafts.UpsertAsync(draft, ct);

        var ack = evt.CanonicalSlugs.Count > maxServices
            ? $"Max {maxServices} services per provider. Keeping: {string.Join(", ", capped)}. Reply YES or EDIT."
            : $"I detected: {string.Join(", ", capped)}. Reply YES to confirm or EDIT to change.";
        await bus.PublishAsync(new SendWhatsAppTextRequested(phone, ack));
    }

    private async Task HandleAddToExistingAsync(
        AdvanceRegistrationDraft evt,
        PhoneNumber phone,
        DateTimeOffset now,
        int maxServices,
        IMessageBus bus,
        CancellationToken ct)
    {
        var existing = await availability.GetAsync(evt.Phone, ct);
        if (existing is null)
        {
            // Listing expired or unlisted while we ran the LLM — silently drop.
            logger.LogDebug("AddToExisting for unlisted {Phone}; dropping", phone.Mask());
            return;
        }

        var newSlugs = evt.CanonicalSlugs.Except(existing.Services).Distinct().ToList();
        if (newSlugs.Count == 0)
        {
            logger.LogDebug("AddToExisting produced no new slugs for {Phone}", phone.Mask());
            var notice = $"You're already listed for {string.Join(", ", existing.Services)} "
                + $"— extended for {options.Value.ExpiryHours}h. Reply LEAVE to unlist.";
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, notice));
            return;
        }

        var remaining = maxServices - existing.Services.Count;
        if (remaining <= 0)
        {
            logger.LogDebug("AddToExisting capped for {Phone}; provider already at {Max}", phone.Mask(), maxServices);
            var cap = $"You're already at the {maxServices}-service cap. "
                + "Reply LEAVE to unlist and start over, "
                + $"or stay listed for {string.Join(", ", existing.Services)}.";
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, cap));
            return;
        }
        var proposed = newSlugs.Take(remaining).ToList();

        var addDraft = RegistrationDraft.Start(evt.Phone, now);
        addDraft.SetServices(proposed, now);
        addDraft.StepTo(RegistrationStep.ConfirmAddServices, now);
        await drafts.UpsertAsync(addDraft, ct);
        var ackAdd = $"I detected: {string.Join(", ", proposed)}. "
            + "Reply YES to add to your listed services, or EDIT to change.";
        await bus.PublishAsync(new SendWhatsAppTextRequested(phone, ackAdd));
    }

    private async Task HandleAppendToDraftAsync(
        AdvanceRegistrationDraft evt,
        PhoneNumber phone,
        DateTimeOffset now,
        int maxServices,
        IMessageBus bus,
        CancellationToken ct)
    {
        var draft = await drafts.GetAsync(evt.Phone, ct);
        if (draft is not { Step: RegistrationStep.ConfirmServices })
        {
            logger.LogDebug("Stale AppendToDraft for {Phone}; draft now at {Step}", phone.Mask(), draft?.Step);
            return;
        }

        var merged = draft.DraftServices.Concat(evt.CanonicalSlugs).Distinct().Take(maxServices).ToList();
        if (merged.Count == draft.DraftServices.Count)
        {
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                $"Already listed: {string.Join(", ", draft.DraftServices)}. Reply YES to confirm or EDIT to change."));
            return;
        }

        draft.SetServices(merged, now);
        await drafts.UpsertAsync(draft, ct);
        await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
            $"Updated: {string.Join(", ", merged)}. Reply YES to confirm or EDIT to change."));
    }

    private async Task HandleAppendToAddDraftAsync(
        AdvanceRegistrationDraft evt,
        PhoneNumber phone,
        DateTimeOffset now,
        int maxServices,
        IMessageBus bus,
        CancellationToken ct)
    {
        var existing = await availability.GetAsync(evt.Phone, ct);
        if (existing is null)
        {
            logger.LogDebug("AppendToAddDraft for unlisted {Phone}; dropping", phone.Mask());
            return;
        }

        var draft = await drafts.GetAsync(evt.Phone, ct);
        if (draft is not { Step: RegistrationStep.ConfirmAddServices })
        {
            logger.LogDebug("Stale AppendToAddDraft for {Phone}; draft now at {Step}", phone.Mask(), draft?.Step);
            return;
        }

        var remainingCap = maxServices - existing.Services.Count;
        var merged = draft.DraftServices
            .Concat(evt.CanonicalSlugs.Where(s => !existing.Services.Contains(s)))
            .Distinct()
            .Take(remainingCap)
            .ToList();
        if (merged.Count == draft.DraftServices.Count)
        {
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                $"Pending add: {string.Join(", ", draft.DraftServices)}. Reply YES to add, or EDIT to change."));
            return;
        }

        draft.SetServices(merged, now);
        await drafts.UpsertAsync(draft, ct);
        await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
            $"Updated: {string.Join(", ", merged)}. Reply YES to add, or EDIT to change."));
    }
}
