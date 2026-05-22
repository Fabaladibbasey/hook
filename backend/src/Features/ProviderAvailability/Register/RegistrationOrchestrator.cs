using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback;
using Hook.Features.Geocoding.Geocode;
using Hook.Features.Geocoding.Models;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Refresh;
using Hook.Features.ProviderAvailability.Register.ExtractServices;
using Hook.Features.ServiceRequest.RequestAggregate;
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

            // Incremental add: a listed provider sending "I offer X" must confirm before
            // the listing mutates. Defer the LLM extract to the outbox so the funnel does
            // not block on Ollama; AdvanceRegistrationDraftHandler promotes the inbound
            // to ConfirmAddServices if extraction yields new slugs. The heartbeat ack
            // below still fires inline so the user always sees a visible reply.
            if (QuickIntent.DetectIntentHint(message.Text) == IntentKind.ProviderRegistration)
            {
                await bus.PublishAsync(new RegistrationExtractServicesRequested(
                    phone.Value, message.Text ?? string.Empty, RegistrationExtractMode.AddToExisting));
            }

            existing.Heartbeat(TimeSpan.FromHours(options.Value.ExpiryHours), now);
            await refreshScheduler.ScheduleAsync(phone.Value, now, ct);
            logger.LogDebug("Heartbeat for active provider {Phone}", phone.Mask());
            var listed = string.Join(", ", existing.Services);
            var hours = options.Value.ExpiryHours;
            var msg =
                $"You're listed as a provider for {listed} (extended for {hours}h). " +
                "To request a different service yourself, send 'I need …'. " +
                "Reply LEAVE to unlist.";
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, msg));
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
            case RegistrationStep.ResolvingServices:
                await HandleResolvingAsync(draft, message, phone, now, ct);
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

    private async Task StartAsync(
        RegistrationDraft draft,
        InboundMessage message,
        PhoneNumber phone,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var text = message.Text ?? string.Empty;
        // Park the draft in ResolvingServices and defer ExtractServices to the outbox so
        // the 60-150s Ollama window doesn't block the user. AdvanceRegistrationDraftHandler
        // advances the draft to ConfirmServices (or back to AwaitingServices on no-slug).
        draft.StepTo(RegistrationStep.ResolvingServices, now);
        await drafts.UpsertAsync(draft, ct);
        await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
            "Looking up the service you mentioned…"));
        await bus.PublishAsync(new RegistrationExtractServicesRequested(
            phone.Value, text, RegistrationExtractMode.NewRegistration));
    }

    private async Task HandleResolvingAsync(
        RegistrationDraft draft,
        InboundMessage message,
        PhoneNumber phone,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // ResolveStartedAt anchors the TTL window: Touch() bumps UpdatedAt on every
        // inbound during Resolving, so gating on UpdatedAt would never elapse.
        var ttl = options.Value.ResolveStuckTtl;
        var resolveStart = draft.ResolveStartedAt ?? draft.UpdatedAt;
        if (now - resolveStart > ttl)
        {
            logger.LogWarning("Resolve stuck > {Ttl}s for {Phone}; reverting to AwaitingServices",
                ttl.TotalSeconds, phone.Mask());
            draft.StepTo(RegistrationStep.AwaitingServices, now);
            await StartAsync(draft, message, phone, now, ct);
            return;
        }
        await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
            "Still looking up your earlier message — one moment."));
    }

    private async Task ConfirmServicesAsync(
        RegistrationDraft draft,
        InboundMessage message,
        PhoneNumber phone,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var quick = QuickIntent.Detect(message.Text);

        // Bracket-style "and X": if the reply is not a confirm/reject/edit/cancel token,
        // defer extract to the outbox so the 60-150s Ollama window doesn't block.
        // AdvanceRegistrationDraftHandler (AppendToDraft mode) merges the canonical
        // slugs into the existing draft and re-prompts.
        if (quick is null
            or not (IntentKind.Confirmation or IntentKind.Rejection or IntentKind.Edit or IntentKind.Cancel))
        {
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "Looking up the service you mentioned…"));
            await bus.PublishAsync(new RegistrationExtractServicesRequested(
                phone.Value, message.Text ?? string.Empty, RegistrationExtractMode.AppendToDraft));
            return;
        }

        if (quick == IntentKind.Confirmation)
        {
            draft.StepTo(RegistrationStep.AwaitingLocation, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(
                phone,
                "Send your location pin (or type your address)."));
            return;
        }

        if (quick == IntentKind.Edit)
        {
            draft.StepTo(RegistrationStep.AwaitingServices, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(
                phone,
                "Send the corrected list of services in one message."));
            return;
        }

        await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Reply YES to confirm or EDIT to change."));
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

        // Mid-confirm append: defer extract to the outbox (mirrors the bracket-style
        // "and X" path in ConfirmServicesAsync). AdvanceRegistrationDraftHandler
        // (AppendToAddDraft mode) merges into the proposal, capped by remaining slots.
        if (quick is null or not (IntentKind.Confirmation or IntentKind.Rejection or IntentKind.Edit))
        {
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "Looking up the service you mentioned…"));
            await bus.PublishAsync(new RegistrationExtractServicesRequested(
                phone.Value, message.Text ?? string.Empty, RegistrationExtractMode.AppendToAddDraft));
            return;
        }

        if (quick == IntentKind.Confirmation)
        {
            existing.AddServices(draft.DraftServices, options.Value.MaxServicesPerProvider);
            existing.Heartbeat(TimeSpan.FromHours(options.Value.ExpiryHours), now);
            await refreshScheduler.ScheduleAsync(phone.Value, now, ct);
            var added = draft.DraftServices.Where(existing.Services.Contains).ToList();
            await drafts.DeleteAsync(phone.Value, ct);
            logger.LogDebug("Added {Count} services for provider {Phone}", added.Count, phone.Mask());
            var addedList = string.Join(", ", added);
            var allServices = string.Join(", ", existing.Services);
            var hours = options.Value.ExpiryHours;
            var msg =
                $"Added {addedList}. You're now listed for {allServices} " +
                $"(extended for {hours}h). Reply LEAVE to unlist.";
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, msg));
            return;
        }

        if (quick == IntentKind.Edit)
        {
            await drafts.DeleteAsync(phone.Value, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "Send the corrected list of services you want to add in one message."));
            return;
        }

        if (quick == IntentKind.Rejection)
        {
            await drafts.DeleteAsync(phone.Value, ct);
            existing.Heartbeat(TimeSpan.FromHours(options.Value.ExpiryHours), now);
            await refreshScheduler.ScheduleAsync(phone.Value, now, ct);
            var listed = string.Join(", ", existing.Services);
            var hours = options.Value.ExpiryHours;
            var msg =
                $"Keeping your listing as is for {listed} (extended for {hours}h). " +
                "Reply LEAVE to unlist.";
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, msg));
            return;
        }

        await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
            "Reply YES to add or EDIT to change."));
    }

    private async Task CollectLocationAsync(
        RegistrationDraft draft,
        InboundMessage message,
        PhoneNumber phone,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (message.Kind == InboundMessageKind.Location && message.Location is { } loc)
        {
            draft.CaptureLocation(loc.Latitude, loc.Longitude, loc.Address ?? loc.Name ?? "(GPS pin)", now);
            draft.StepTo(RegistrationStep.AwaitingConsent, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(
                phone,
                "Got your location. Share your phone with clients on match? " +
                "Reply YES to share, NO to keep it private."));
            return;
        }

        if (message.Kind == InboundMessageKind.Text && !string.IsNullOrWhiteSpace(message.Text))
        {
            // Defer geocoding HTTP off the inbound critical path (~10s Google timeout).
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "Looking up that address — one sec…"));
            await bus.PublishAsync(new GeocodeAddressRequested(
                phone.Value, message.Text!, GeocodeFlow.Provider, DraftStampedAt: draft.UpdatedAt));
            return;
        }

        await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Send your location pin or type your address."));
    }

    private async Task ConfirmLocationAsync(
        RegistrationDraft draft,
        InboundMessage message,
        PhoneNumber phone,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (message.Kind == InboundMessageKind.Location && message.Location is { } loc)
        {
            draft.CaptureLocation(loc.Latitude, loc.Longitude, loc.Address ?? loc.Name ?? "(GPS pin)", now);
            draft.StepTo(RegistrationStep.AwaitingConsent, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(
                phone,
                "Got your location. Share your phone with clients on match? " +
                "Reply YES to share, NO to keep it private."));
            return;
        }

        if (QuickIntent.Detect(message.Text) == IntentKind.Confirmation)
        {
            draft.StepTo(RegistrationStep.AwaitingConsent, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(
                phone,
                "Share your phone with clients on match? " +
                "Reply YES to share, NO to keep it private."));
            return;
        }

        await bus.PublishAsync(new SendWhatsAppTextRequested(
            phone,
            "Reply YES to confirm this address, or send your GPS pin instead."));
    }

    private async Task CollectConsentAsync(
        RegistrationDraft draft,
        InboundMessage message,
        PhoneNumber phone,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var quick = QuickIntent.Detect(message.Text);
        bool? consent = quick switch
        {
            IntentKind.Confirmation => true,
            IntentKind.Rejection => false,
            _ => null
        };

        if (consent is null)
        {
            await bus.PublishAsync(new SendWhatsAppTextRequested(
                phone,
                "Share your phone with clients on match? " +
                "Reply YES to share, NO to keep it private."));
            return;
        }

        draft.SetShareContact(consent.Value, now);

        if (draft.DraftLatitude is null || draft.DraftLongitude is null || draft.DraftServices.Count == 0)
        {
            logger.LogWarning("Incomplete draft for {Phone} cannot finalize", phone.Mask());
            await drafts.DeleteAsync(phone.Value, ct);
            var msg =
                "Couldn't list you — I'm missing your service or location. " +
                "Reply \"I offer …\" and send a location pin to try again.";
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, msg));
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
            var msg =
                "You can't be both client and provider for the same service. " +
                $"You have an open request for {human} — reply CANCEL to close it, " +
                "then register again.";
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, msg));
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

            var hours = options.Value.ExpiryHours;
            var msg =
                $"You are listed for {hours}h. " +
                "Reply 'I offer …' anytime to update your services, " +
                "or LEAVE to unlist.";
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, msg));
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
