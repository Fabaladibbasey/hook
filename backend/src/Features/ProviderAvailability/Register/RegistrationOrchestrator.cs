using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback;
using Hook.Features.Geocoding.Geocode;
using Hook.Features.Geocoding.Models;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Refresh;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.ServiceTaxonomy.ResolveSlug;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.ProviderAvailability.Register;

public sealed class RegistrationOrchestrator(
    IRegistrationDraftRepository drafts,
    IProviderAvailabilityRepository availability,
    IServiceRequestRepository requests,
    IFeedbackRepository feedback,
    IConversationAi ai,
    SlugResolver slugResolver,
    GeocodingService geocoding,
    IMessageBus bus,
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
            // Listed providers can unlist by replying with any cancel-class token
            // (leave/end/bye/quit/done/exit/goodbye/cancel). The heartbeat reply
            // explicitly instructs "Reply LEAVE to unlist", so the handler must
            // honour that intent before extending the TTL.
            var quick = QuickIntent.Detect(message.Text);
            if (quick == IntentKind.Cancel)
            {
                await availability.RemoveAsync(phone.Value, ct);
                await drafts.DeleteAsync(phone.Value, ct);
                await feedback.DeleteStatsAsync(phone.Value, ct);
                logger.LogDebug("Unlisted provider {Phone}", phone.Mask());
                await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                    "You are unlisted. Reply 'I offer …' to list again, or 'I need …' to request a service."));
                return;
            }

            // Pending add-service confirmation: a previous inbound proposed new
            // slugs and asked YES/EDIT. Drive that flow before treating this
            // inbound as a fresh proposal.
            var pendingDraft = await drafts.GetAsync(phone.Value, ct);
            if (pendingDraft is { Step: RegistrationStep.ConfirmAddServices })
            {
                pendingDraft.Touch(now);
                await ConfirmAddServicesAsync(existing, pendingDraft, message, phone, now, ct);
                return;
            }

            // Incremental add: a listed provider sending "I offer X" must
            // confirm before the listing mutates — symmetric to the new-
            // registration YES/EDIT step. TTL extension is deferred until
            // confirmation so a stale probe can't extend a listing.
            var proposed = await ProposeAddServicesAsync(existing, message.Text ?? string.Empty, ct);
            if (proposed.Count > 0)
            {
                var addDraft = RegistrationDraft.Start(phone.Value, now);
                addDraft.SetServices(proposed, now);
                addDraft.StepTo(RegistrationStep.ConfirmAddServices, now);
                await drafts.UpsertAsync(addDraft, ct);
                await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                    $"I detected: {string.Join(", ", proposed)}. Reply YES to add to your listed services, or EDIT to change."));
                return;
            }

            existing.Heartbeat(TimeSpan.FromHours(options.Value.ExpiryHours), now);
            await refreshScheduler.ScheduleAsync(phone.Value, now, ct);
            logger.LogDebug("Heartbeat for active provider {Phone}", phone.Mask());
            var listed = string.Join(", ", existing.Services);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                $"You're listed as a provider for {listed} (extended for {options.Value.ExpiryHours}h). To request a different service yourself, send 'I need …'. Reply LEAVE to unlist."));
            return;
        }

        var existingDraft = await drafts.GetAsync(phone.Value, ct);
        // ConfirmAddServices is a listed-provider state. If it survived after
        // the provider was unlisted (retention sweep, manual cleanup), treat
        // as corrupt — drop it and restart the new-registration flow.
        if (existingDraft is { Step: RegistrationStep.ConfirmAddServices })
        {
            await drafts.DeleteAsync(phone.Value, ct);
            existingDraft = null;
        }
        var draft = existingDraft ?? RegistrationDraft.Start(phone.Value, now);
        if (existingDraft is not null) draft.Touch(now);

        switch (draft.Step)
        {
            case RegistrationStep.AwaitingServices:
                await StartAsync(draft, message, phone, now, ct);
                break;
            case RegistrationStep.ConfirmServices:
                await ConfirmServicesAsync(draft, message, phone, now, ct);
                break;
            case RegistrationStep.AwaitingLocation:
                await CollectLocationAsync(draft, message, phone, now, ct);
                break;
            case RegistrationStep.ConfirmLocation:
                await ConfirmLocationAsync(draft, message, phone, now, ct);
                break;
            case RegistrationStep.AwaitingConsent:
                await CollectConsentAsync(draft, message, phone, now, ct);
                break;
            default:
                logger.LogWarning("Unexpected draft step {Step} for {Phone}", draft.Step, phone.Mask());
                break;
        }
    }

    /// Extends the TTL of an already-listed provider without sending any WhatsApp reply.
    /// Caller (router) sends a tailored ack so we don't double-message on greetings.
    public async Task<AvailabilityAggregate.ProviderAvailability?> HeartbeatSilentAsync(
        PhoneNumber phone, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await availability.GetAsync(phone.Value, ct);
        if (existing is null) return null;

        existing.Heartbeat(TimeSpan.FromHours(options.Value.ExpiryHours), now);
        await refreshScheduler.ScheduleAsync(phone.Value, now, ct);
        logger.LogDebug("Silent heartbeat for active provider {Phone}", phone.Mask());
        return existing;
    }

    private async Task StartAsync(RegistrationDraft draft, InboundMessage message, PhoneNumber phone, DateTimeOffset now, CancellationToken ct)
    {
        var text = message.Text ?? string.Empty;
        var extracted = await ai.ExtractServicesAsync(text, ct);
        if (extracted.Slugs.Count == 0)
        {
            // Symmetric to ClientRequestOrchestrator: keep the REQUEST exit open in
            // case the user landed in registration on a borderline classification.
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "Tell me what services you offer (e.g. plumbing, carpentry, computer repair) — or reply REQUEST if you need a service instead."));
            await drafts.UpsertAsync(draft, ct);
            return;
        }

        var canonicalSlugs = await ResolveAllAsync(extracted.Slugs, text, ct);
        var capped = canonicalSlugs.Distinct().Take(options.Value.MaxServicesPerProvider).ToList();
        // Draft persist + outbound prompt are part of the same Wolverine handler EF transaction;
        // commit lands at handler-end. A "fast yes" reply over WhatsApp can't race the commit in
        // practice (round-trip > commit latency).
        draft.SetServices(capped, now);
        draft.StepTo(RegistrationStep.ConfirmServices, now);
        await drafts.UpsertAsync(draft, ct);

        if (canonicalSlugs.Count > options.Value.MaxServicesPerProvider)
        {
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                $"Max {options.Value.MaxServicesPerProvider} services per provider. Keeping: {string.Join(", ", capped)}. Reply YES or EDIT."));
        }
        else
        {
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                $"I detected: {string.Join(", ", capped)}. Reply YES to confirm or EDIT to change."));
        }
    }

    private async Task ConfirmServicesAsync(RegistrationDraft draft, InboundMessage message, PhoneNumber phone, DateTimeOffset now, CancellationToken ct)
    {
        var quick = QuickIntent.Detect(message.Text);

        // Bracket-style "and X": if the reply is not a confirm/reject/edit/cancel token,
        // try to extract another service and append before falling through to the
        // standard yes/edit handling. Lets a provider register multiple services across
        // separate messages instead of cramming them into a single offer.
        if (quick is null or not (IntentKind.Confirmation or IntentKind.Rejection or IntentKind.Edit or IntentKind.Cancel))
        {
            var extracted = await ai.ExtractServicesAsync(message.Text ?? string.Empty, ct);
            if (extracted.Slugs.Count > 0)
            {
                var canonical = await ResolveAllAsync(extracted.Slugs, message.Text ?? string.Empty, ct);
                var merged = draft.DraftServices.Concat(canonical).Distinct()
                    .Take(options.Value.MaxServicesPerProvider).ToList();
                if (merged.Count != draft.DraftServices.Count)
                {
                    draft.SetServices(merged, now);
                    await drafts.UpsertAsync(draft, ct);
                    await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                        $"Updated: {string.Join(", ", merged)}. Reply YES to confirm or EDIT to change."));
                    return;
                }
            }
        }

        var intent = quick is { } q
            ? new IntentDetectionResult(q, 1.0, "en", "quick")
            : await ai.DetectIntentAsync(message.Text ?? string.Empty, ct);
        if (intent.Intent == IntentKind.Confirmation)
        {
            draft.StepTo(RegistrationStep.AwaitingLocation, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Send your location pin (or type your address)."));
            return;
        }

        if (intent.Intent == IntentKind.Edit)
        {
            draft.StepTo(RegistrationStep.AwaitingServices, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Send the corrected list of services in one message."));
            return;
        }

        await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Reply YES to confirm or EDIT to change."));
    }

    private async Task<List<string>> ResolveAllAsync(IEnumerable<string> slugs, string originalText, CancellationToken ct)
    {
        var canonical = new List<string>();
        foreach (var slug in slugs)
        {
            var resolved = await slugResolver.ResolveAsync(slug, originalText, ct);
            canonical.Add(resolved.CanonicalSlug);
        }
        return canonical;
    }

    private async Task<List<string>> ProposeAddServicesAsync(
        AvailabilityAggregate.ProviderAvailability existing, string text, CancellationToken ct)
    {
        var extracted = await ai.ExtractServicesAsync(text, ct);
        if (extracted.Slugs.Count == 0) return [];

        var canonical = await ResolveAllAsync(extracted.Slugs, text, ct);
        var newSlugs = canonical.Except(existing.Services).Distinct().ToList();
        if (newSlugs.Count == 0) return [];

        var remainingCap = options.Value.MaxServicesPerProvider - existing.Services.Count;
        if (remainingCap <= 0) return [];
        return [.. newSlugs.Take(remainingCap)];
    }

    private async Task ConfirmAddServicesAsync(
        AvailabilityAggregate.ProviderAvailability existing,
        RegistrationDraft draft,
        InboundMessage message,
        PhoneNumber phone,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var quick = QuickIntent.Detect(message.Text);

        // Mid-confirm append: not a yes/no/edit token, try to extract more
        // services and merge into the proposal. Mirrors the bracket-style
        // "and X" path in ConfirmServicesAsync for the new-registration flow.
        if (quick is null or not (IntentKind.Confirmation or IntentKind.Rejection or IntentKind.Edit))
        {
            var extracted = await ai.ExtractServicesAsync(message.Text ?? string.Empty, ct);
            if (extracted.Slugs.Count > 0)
            {
                var canonical = await ResolveAllAsync(extracted.Slugs, message.Text ?? string.Empty, ct);
                var remainingCap = options.Value.MaxServicesPerProvider - existing.Services.Count;
                var merged = draft.DraftServices
                    .Concat(canonical.Where(s => !existing.Services.Contains(s)))
                    .Distinct()
                    .Take(remainingCap)
                    .ToList();
                if (merged.Count != draft.DraftServices.Count)
                {
                    draft.SetServices(merged, now);
                    await drafts.UpsertAsync(draft, ct);
                    await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                        $"Updated: {string.Join(", ", merged)}. Reply YES to add, or EDIT to change."));
                    return;
                }
            }
        }

        var intent = quick is { } q
            ? new IntentDetectionResult(q, 1.0, "en", "quick")
            : await ai.DetectIntentAsync(message.Text ?? string.Empty, ct);

        if (intent.Intent == IntentKind.Confirmation)
        {
            existing.AddServices(draft.DraftServices, options.Value.MaxServicesPerProvider);
            existing.Heartbeat(TimeSpan.FromHours(options.Value.ExpiryHours), now);
            await refreshScheduler.ScheduleAsync(phone.Value, now, ct);
            var added = draft.DraftServices.Where(existing.Services.Contains).ToList();
            await drafts.DeleteAsync(phone.Value, ct);
            logger.LogDebug("Added {Count} services for provider {Phone}", added.Count, phone.Mask());
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                $"Added {string.Join(", ", added)}. You're now listed for {string.Join(", ", existing.Services)} (extended for {options.Value.ExpiryHours}h). Reply LEAVE to unlist."));
            return;
        }

        if (intent.Intent == IntentKind.Edit)
        {
            await drafts.DeleteAsync(phone.Value, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "Send the corrected list of services you want to add in one message."));
            return;
        }

        if (intent.Intent == IntentKind.Rejection)
        {
            await drafts.DeleteAsync(phone.Value, ct);
            existing.Heartbeat(TimeSpan.FromHours(options.Value.ExpiryHours), now);
            await refreshScheduler.ScheduleAsync(phone.Value, now, ct);
            var listed = string.Join(", ", existing.Services);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                $"Keeping your listing as is for {listed} (extended for {options.Value.ExpiryHours}h). Reply LEAVE to unlist."));
            return;
        }

        await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
            "Reply YES to add or EDIT to change."));
    }

    private async Task CollectLocationAsync(RegistrationDraft draft, InboundMessage message, PhoneNumber phone, DateTimeOffset now, CancellationToken ct)
    {
        if (message.Kind == InboundMessageKind.Location && message.Location is { } loc)
        {
            draft.CaptureLocation(loc.Latitude, loc.Longitude, loc.Address ?? loc.Name ?? "(GPS pin)", now);
            draft.StepTo(RegistrationStep.AwaitingConsent, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Got your location. Share your phone with clients on match? Reply YES to share, NO to keep it private."));
            return;
        }

        if (message.Kind == InboundMessageKind.Text && !string.IsNullOrWhiteSpace(message.Text))
        {
            var geocoded = await geocoding.GeocodeAsync(message.Text!, ct);
            if (geocoded is null)
            {
                await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "I couldn't find that address. Try typing it differently, or send a GPS pin (📎 → Location)."));
                await drafts.UpsertAsync(draft, ct);
                return;
            }

            draft.CaptureLocation(geocoded.Location.Latitude, geocoded.Location.Longitude, geocoded.FormattedAddress, now);
            draft.StepTo(RegistrationStep.ConfirmLocation, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                $"Found: '{geocoded.FormattedAddress}'. Reply YES to confirm or send your GPS pin instead."));
            return;
        }

        await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Send your location pin or type your address."));
    }

    private async Task ConfirmLocationAsync(RegistrationDraft draft, InboundMessage message, PhoneNumber phone, DateTimeOffset now, CancellationToken ct)
    {
        if (message.Kind == InboundMessageKind.Location && message.Location is { } loc)
        {
            draft.CaptureLocation(loc.Latitude, loc.Longitude, loc.Address ?? loc.Name ?? "(GPS pin)", now);
            draft.StepTo(RegistrationStep.AwaitingConsent, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Got your location. Share your phone with clients on match? Reply YES to share, NO to keep it private."));
            return;
        }

        var quick = QuickIntent.Detect(message.Text);
        var intent = quick is { } q
            ? new IntentDetectionResult(q, 1.0, "en", "quick")
            : await ai.DetectIntentAsync(message.Text ?? string.Empty, ct);
        if (intent.Intent == IntentKind.Confirmation)
        {
            draft.StepTo(RegistrationStep.AwaitingConsent, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Share your phone with clients on match? Reply YES to share, NO to keep it private."));
            return;
        }

        await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Reply YES to confirm this address, or send your GPS pin instead."));
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
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Share your phone with clients on match? Reply YES to share, NO to keep it private."));
            return;
        }

        draft.SetShareContact(consent.Value, now);

        if (draft.DraftLatitude is null || draft.DraftLongitude is null || draft.DraftServices.Count == 0)
        {
            logger.LogWarning("Incomplete draft for {Phone} cannot finalize", phone.Mask());
            await drafts.DeleteAsync(phone.Value, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "Couldn't list you — I'm missing your service or location. Reply \"I offer …\" and send a location pin to try again."));
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
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                $"You can't be both client and provider for the same service. You have an open request for {human} — reply CANCEL to close it, then register again."));
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
            await drafts.DeleteAsync(phone.Value, ct);
            await refreshScheduler.ScheduleAsync(phone.Value, now, ct);

            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                $"You are listed for {options.Value.ExpiryHours}h. Reply 'I offer …' anytime to update your services, or LEAVE to unlist."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to finalize provider registration for {Phone}", phone.Mask());
            await drafts.DeleteAsync(phone.Value, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "Something went wrong listing you. Try again in a moment — reply \"I offer …\" to retry."));
        }
    }
}
