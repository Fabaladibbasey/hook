using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Geocoding.Geocode;
using Hook.Features.Geocoding.Models;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Refresh;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.ServiceTaxonomy.ResolveSlug;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Options;

namespace Hook.Features.ProviderAvailability.Register;

public sealed class RegistrationOrchestrator(
    IRegistrationDraftRepository drafts,
    IProviderAvailabilityRepository availability,
    IServiceRequestRepository requests,
    IConversationAi ai,
    SlugResolver slugResolver,
    GeocodingService geocoding,
    IWhatsappClient whatsapp,
    ProviderRefreshScheduler refreshScheduler,
    IOptions<ProviderAvailabilityOptions> options,
    TimeProvider clock,
    ILogger<RegistrationOrchestrator> logger)
{
    public async Task HandleAsync(InboundMessage message, CancellationToken ct = default)
    {
        var phone = message.From;
        var now = clock.GetUtcNow();

        var existing = await availability.GetAsync(phone.Value, ct);
        if (existing is not null)
        {
            existing.Heartbeat(TimeSpan.FromHours(options.Value.ExpiryHours), now);
            await availability.SaveChangesAsync(ct);
            await refreshScheduler.ScheduleAsync(phone.Value, now, ct);
            logger.LogDebug("Heartbeat for active provider {Phone}", phone.Mask());
            var listed = string.Join(", ", existing.Services);
            await whatsapp.SendTextAsync(phone,
                $"You're listed as a provider for {listed} (extended for {options.Value.ExpiryHours}h). To request a different service yourself, send 'I need …'. Reply LEAVE to unlist.", ct);
            return;
        }

        var draft = await drafts.GetAsync(phone.Value, ct) ?? RegistrationDraft.Start(phone.Value, now);
        draft.UpdatedAt = now;

        switch (draft.Step)
        {
            case RegistrationStep.AwaitingServices:
                await StartAsync(draft, message, phone, ct);
                break;
            case RegistrationStep.ConfirmServices:
                await ConfirmServicesAsync(draft, message, phone, ct);
                break;
            case RegistrationStep.AwaitingLocation:
                await CollectLocationAsync(draft, message, phone, ct);
                break;
            case RegistrationStep.ConfirmLocation:
                await ConfirmLocationAsync(draft, message, phone, ct);
                break;
            case RegistrationStep.AwaitingConsent:
                await CollectConsentAsync(draft, message, phone, now, ct);
                break;
            default:
                logger.LogWarning("Unexpected draft step {Step} for {Phone}", draft.Step, phone.Mask());
                break;
        }
    }

    private async Task StartAsync(RegistrationDraft draft, InboundMessage message, PhoneNumber phone, CancellationToken ct)
    {
        var text = message.Text ?? string.Empty;
        var extracted = await ai.ExtractServicesAsync(text, ct);
        if (extracted.Slugs.Count == 0)
        {
            await whatsapp.SendTextAsync(phone, "Tell me what services you offer (e.g. plumbing, carpentry, computer repair).", ct);
            await drafts.UpsertAsync(draft, ct);
            return;
        }

        var canonicalSlugs = new List<string>();
        foreach (var slug in extracted.Slugs)
        {
            var resolved = await slugResolver.ResolveAsync(slug, text, ct);
            canonicalSlugs.Add(resolved.CanonicalSlug);
        }

        var capped = canonicalSlugs.Distinct().Take(options.Value.MaxServicesPerProvider).ToList();
        // Persist the draft BEFORE sending the prompt so a fast "yes" reply
        // doesn't race ahead of the save and miss the draft in InboundRouter.
        draft.DraftServices = capped;
        draft.Step = RegistrationStep.ConfirmServices;
        await drafts.UpsertAsync(draft, ct);

        if (canonicalSlugs.Count > options.Value.MaxServicesPerProvider)
        {
            await whatsapp.SendTextAsync(phone,
                $"Max {options.Value.MaxServicesPerProvider} services per provider. Keeping: {string.Join(", ", capped)}. Reply YES or EDIT.", ct);
        }
        else
        {
            await whatsapp.SendTextAsync(phone,
                $"I detected: {string.Join(", ", capped)}. Reply YES to confirm or EDIT to change.", ct);
        }
    }

    private async Task ConfirmServicesAsync(RegistrationDraft draft, InboundMessage message, PhoneNumber phone, CancellationToken ct)
    {
        var quick = QuickIntent.Detect(message.Text);
        var intent = quick is { } q
            ? new IntentDetectionResult(q, 1.0, "en", "quick")
            : await ai.DetectIntentAsync(message.Text ?? string.Empty, ct);
        if (intent.Intent == IntentKind.Confirmation)
        {
            draft.Step = RegistrationStep.AwaitingLocation;
            await drafts.UpsertAsync(draft, ct);
            await whatsapp.SendTextAsync(phone, "Send your location pin (or type your address).", ct);
            return;
        }

        if (intent.Intent == IntentKind.Edit)
        {
            draft.Step = RegistrationStep.AwaitingServices;
            await drafts.UpsertAsync(draft, ct);
            await whatsapp.SendTextAsync(phone, "Send the corrected list of services in one message.", ct);
            return;
        }

        await whatsapp.SendTextAsync(phone, "Reply YES to confirm or EDIT to change.", ct);
    }

    private async Task CollectLocationAsync(RegistrationDraft draft, InboundMessage message, PhoneNumber phone, CancellationToken ct)
    {
        if (message.Kind == InboundMessageKind.Location && message.Location is { } loc)
        {
            draft.DraftLatitude = loc.Latitude;
            draft.DraftLongitude = loc.Longitude;
            draft.DraftFormattedAddress = loc.Address ?? loc.Name ?? "(GPS pin)";
            draft.Step = RegistrationStep.AwaitingConsent;
            await drafts.UpsertAsync(draft, ct);
            await whatsapp.SendTextAsync(phone, "Got your location. Share your phone with clients on match? Reply YES to share, NO to keep it private.", ct);
            return;
        }

        if (message.Kind == InboundMessageKind.Text && !string.IsNullOrWhiteSpace(message.Text))
        {
            var geocoded = await geocoding.GeocodeAsync(message.Text!, ct);
            if (geocoded is null)
            {
                await whatsapp.SendTextAsync(phone, "Couldn't find that address. Please send your GPS pin (📎 → Location).", ct);
                await drafts.UpsertAsync(draft, ct);
                return;
            }

            draft.DraftLatitude = geocoded.Location.Latitude;
            draft.DraftLongitude = geocoded.Location.Longitude;
            draft.DraftFormattedAddress = geocoded.FormattedAddress;
            draft.Step = RegistrationStep.ConfirmLocation;
            await drafts.UpsertAsync(draft, ct);
            await whatsapp.SendTextAsync(phone,
                $"Found: '{geocoded.FormattedAddress}'. Reply YES to confirm or send your GPS pin instead.", ct);
            return;
        }

        await whatsapp.SendTextAsync(phone, "Send your location pin or type your address.", ct);
    }

    private async Task ConfirmLocationAsync(RegistrationDraft draft, InboundMessage message, PhoneNumber phone, CancellationToken ct)
    {
        if (message.Kind == InboundMessageKind.Location && message.Location is { } loc)
        {
            draft.DraftLatitude = loc.Latitude;
            draft.DraftLongitude = loc.Longitude;
            draft.DraftFormattedAddress = loc.Address ?? loc.Name ?? "(GPS pin)";
            draft.Step = RegistrationStep.AwaitingConsent;
            await drafts.UpsertAsync(draft, ct);
            await whatsapp.SendTextAsync(phone, "Got your location. Share your phone with clients on match? Reply YES to share, NO to keep it private.", ct);
            return;
        }

        var quick = QuickIntent.Detect(message.Text);
        var intent = quick is { } q
            ? new IntentDetectionResult(q, 1.0, "en", "quick")
            : await ai.DetectIntentAsync(message.Text ?? string.Empty, ct);
        if (intent.Intent == IntentKind.Confirmation)
        {
            draft.Step = RegistrationStep.AwaitingConsent;
            await drafts.UpsertAsync(draft, ct);
            await whatsapp.SendTextAsync(phone, "Share your phone with clients on match? Reply YES to share, NO to keep it private.", ct);
            return;
        }

        await whatsapp.SendTextAsync(phone, "Reply YES to confirm this address, or send your GPS pin instead.", ct);
    }

    private async Task CollectConsentAsync(RegistrationDraft draft, InboundMessage message, PhoneNumber phone, DateTimeOffset now, CancellationToken ct)
    {
        var quick = QuickIntent.Detect(message.Text);
        var intent = quick is { } q
            ? new IntentDetectionResult(q, 1.0, "en", "quick")
            : await ai.DetectIntentAsync(message.Text ?? string.Empty, ct);
        bool? consent = intent.Intent switch
        {
            IntentKind.Confirmation => true,
            IntentKind.Rejection => false,
            _ => null
        };

        if (consent is null)
        {
            await whatsapp.SendTextAsync(phone, "Share your phone with clients on match? Reply YES to share, NO to keep it private.", ct);
            return;
        }

        draft.DraftShareContact = consent.Value;

        if (draft.DraftLatitude is null || draft.DraftLongitude is null || draft.DraftServices.Count == 0)
        {
            logger.LogWarning("Incomplete draft for {Phone} cannot finalize", phone.Mask());
            await drafts.DeleteAsync(phone.Value, ct);
            await whatsapp.SendTextAsync(phone,
                "Couldn't list you — some details were missing. Reply 'I offer …' to start over.", ct);
            return;
        }

        // Reject same-service dual role: if the user has an open request for one
        // of the services they're trying to list, listing makes no sense (they'd
        // be matched to themselves and serve their own request).
        // Block on any non-closed request (Open or Matched). Matching pipeline runs
        // async and may flip the status from Open to Matched between funnel steps —
        // both states still represent an active engagement that conflicts with
        // listing for the same service.
        var openRequest = await requests.GetActiveByClientAsync(phone.Value, ct);
        if (openRequest is not null &&
            openRequest.Status != ServiceRequestStatus.Closed &&
            draft.DraftServices.Contains(openRequest.ServiceSlug))
        {
            var human = openRequest.ServiceSlug.Replace('-', ' ');
            logger.LogDebug("Rejecting same-service registration for {Phone} slug={Slug}",
                phone.Mask(), openRequest.ServiceSlug);
            await drafts.DeleteAsync(phone.Value, ct);
            await whatsapp.SendTextAsync(phone,
                $"You have an open request for {human}. Cancel it first (reply LEAVE) before listing yourself for {human}.", ct);
            return;
        }

        try
        {
            var availabilityRow = AvailabilityAggregate.ProviderAvailability.Register(
                phone.Value,
                draft.DraftServices,
                new Location(draft.DraftLatitude.Value, draft.DraftLongitude.Value),
                draft.DraftFormattedAddress,
                consent.Value,
                TimeSpan.FromHours(options.Value.ExpiryHours),
                now);

            await availability.AddAsync(availabilityRow, ct);
            await availability.SaveChangesAsync(ct);
            await drafts.DeleteAsync(phone.Value, ct);
            await refreshScheduler.ScheduleAsync(phone.Value, now, ct);

            await whatsapp.SendTextAsync(phone,
                $"You are listed for {options.Value.ExpiryHours}h. Reply 'I offer …' anytime to update your services, or LEAVE to unlist.", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to finalize provider registration for {Phone}", phone.Mask());
            await drafts.DeleteAsync(phone.Value, ct);
            await whatsapp.SendTextAsync(phone,
                "Couldn't list you right now. Reply 'I offer …' to try again.", ct);
        }
    }
}
