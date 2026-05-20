using System.Text.RegularExpressions;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Geocoding.Geocode;
using Hook.Features.Matching;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ServiceRequest.Create.ExtractServices;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Options;
using Wolverine;
using Location = Hook.Features.Geocoding.Models.Location;

namespace Hook.Features.ServiceRequest.Create;

public sealed class ClientRequestOrchestrator(
    IClientRequestDraftRepository drafts,
    IServiceRequestRepository requests,
    IProviderAvailabilityRepository availability,
    IMessageBus bus,
    IOptions<MatchingOptions> matchingOptions,
    TimeProvider clock,
    ILogger<ClientRequestOrchestrator> logger)
{
    public async Task HandleAsync(InboundMessage message, CancellationToken ct = default)
    {
        var phone = message.From;
        var now = clock.GetUtcNow();
        var existing = await drafts.GetAsync(phone.Value, ct);
        var draft = existing ?? ClientRequestDraft.Start(phone.Value, now);
        if (existing is not null) draft.Touch(now);

        // Slug switch mid-funnel: if the user is at a location step and sends a strong
        // service-request hint, defer the extract + slug resolve to the outbox so the
        // 60-150s Ollama window doesn't block the funnel. The hint guard keeps us off the
        // LLM for every location reply. AwaitingDescription is intentionally excluded —
        // we just asked the user to describe their problem, so a problem statement there
        // IS the description, not a new service intent. AdvanceClientRequestDraftHandler
        // race-guards against the user advancing the draft while the LLM runs.
        if (draft.Step is ClientRequestStep.AwaitingLocation
                       or ClientRequestStep.ConfirmLocation
            && QuickIntent.DetectIntentHint(message.Text) == IntentKind.ServiceRequest)
        {
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "Looking up the service you mentioned…"));
            await bus.PublishAsync(new ExtractServicesRequested(phone.Value, message.Text ?? string.Empty, IsSwitch: true));
            return;
        }

        switch (draft.Step)
        {
            case ClientRequestStep.AwaitingService:
                await StartAsync(draft, message, phone, now, ct);
                break;
            case ClientRequestStep.ResolvingService:
                await HandleResolvingAsync(draft, message, phone, now, ct);
                break;
            case ClientRequestStep.ConfirmService:
                await ConfirmServiceAsync(draft, message, phone, now, ct);
                break;
            case ClientRequestStep.AwaitingLocation:
                await CollectLocationAsync(draft, message, phone, now, ct);
                break;
            case ClientRequestStep.ConfirmLocation:
                await ConfirmLocationAsync(draft, message, phone, now, ct);
                break;
            case ClientRequestStep.AwaitingDescription:
                await CollectDescriptionAsync(draft, message, phone, now, ct);
                break;
            case ClientRequestStep.AwaitingPhoneShareConsent:
                await CollectPhoneShareConsentAsync(draft, message, phone, now, ct);
                break;
            default:
                logger.LogWarning("Unexpected draft step {Step} for {Phone}", draft.Step, phone.Mask());
                break;
        }
    }

    private async Task StartAsync(ClientRequestDraft draft, InboundMessage message, PhoneNumber phone, DateTimeOffset now, CancellationToken ct)
    {
        var text = message.Text ?? string.Empty;
        // Park the draft in ResolvingService and defer ExtractServices to the outbox so
        // the 60-150s Ollama window doesn't block the user. AdvanceClientRequestDraftHandler
        // advances the draft to ConfirmService (or back to AwaitingService on no-slug).
        draft.StepTo(ClientRequestStep.ResolvingService, now);
        await drafts.UpsertAsync(draft, ct);
        await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
            "Looking up the service you mentioned…"));
        await bus.PublishAsync(new ExtractServicesRequested(phone.Value, text, IsSwitch: false));
    }

    private async Task HandleResolvingAsync(ClientRequestDraft draft, InboundMessage message, PhoneNumber phone, DateTimeOffset now, CancellationToken ct)
    {
        // ResolveStartedAt anchors the TTL window: Touch() bumps UpdatedAt on every
        // inbound during Resolving, so gating on UpdatedAt would never elapse.
        var ttl = matchingOptions.Value.ResolveStuckTtl;
        var resolveStart = draft.ResolveStartedAt ?? draft.UpdatedAt;
        if (now - resolveStart > ttl)
        {
            // The previous resolve dead-lettered or the host crashed before AdvanceClientRequestDraft
            // ran. Force-revert to AwaitingService and treat the current message as a fresh start so
            // the user is not trapped.
            logger.LogWarning("Resolve stuck > {Ttl}s for {Phone}; reverting to AwaitingService", ttl.TotalSeconds, phone.Mask());
            draft.StepTo(ClientRequestStep.AwaitingService, now);
            await StartAsync(draft, message, phone, now, ct);
            return;
        }
        await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
            "Still looking up your earlier message — one moment."));
    }

    // "no I want carpentry", "no actually plumbing" — the leading "no" rejects
    // the proposed slug, but the trailing text describes a NEW service. Replay
    // the trailing text through StartAsync so the user gets a fresh ConfirmService
    // in one turn instead of "What service?" → user-retypes → ConfirmService.
    // Provider-intent rephrases ("no I am a teacher") are handled upstream by
    // the router's cross-flow switch on ProviderRegistration hints.
    private static readonly Regex LeadingNoRx = new(
        @"^\s*(no|nope|nah)\b[\s,.:;!?-]*(?<rest>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private async Task ConfirmServiceAsync(ClientRequestDraft draft, InboundMessage message, PhoneNumber phone, DateTimeOffset now, CancellationToken ct)
    {
        var leadingNo = LeadingNoRx.Match(message.Text ?? string.Empty);
        if (leadingNo.Success)
        {
            var rest = leadingNo.Groups["rest"].Value.Trim();
            if (rest.Length > 0 && QuickIntent.Detect(rest) is null)
            {
                draft.SwitchSlug(string.Empty, now);
                draft.StepTo(ClientRequestStep.AwaitingService, now);
                var replay = message with { Text = rest };
                await StartAsync(draft, replay, phone, now, ct);
                return;
            }
        }

        // QuickIntent's regex covers YES/NO/OK/etc. at confirm steps; the LLM fallback
        // here was a 60-150s tax for negligible UX gain, so we re-prompt instead.
        var intentKind = QuickIntent.Detect(message.Text);
        if (intentKind == IntentKind.Confirmation)
        {
            // If we already have a captured location (slug-switch path), skip the
            // location collection steps and jump straight to description.
            if (draft.DraftLatitude is not null && draft.DraftLongitude is not null)
            {
                draft.StepTo(ClientRequestStep.AwaitingDescription, now);
                await drafts.UpsertAsync(draft, ct);
                await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                    $"Got it. Using your saved location: {draft.DraftFormattedAddress}. Want to add a description? Send it now or reply SKIP."));
                return;
            }

            draft.StepTo(ClientRequestStep.AwaitingLocation, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Send your location pin (or type your address)."));
            return;
        }
        if (intentKind == IntentKind.Rejection)
        {
            draft.SwitchSlug(string.Empty, now);
            draft.StepTo(ClientRequestStep.AwaitingService, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "What service do you need? Or reply REGISTER if you're offering services instead."));
            return;
        }
        await bus.PublishAsync(new SendWhatsAppTextRequested(phone, $"Please reply YES or NO — YES to confirm {draft.DraftServiceSlug.Replace('-', ' ')}, NO to choose another service."));
    }

    private async Task CollectLocationAsync(ClientRequestDraft draft, InboundMessage message, PhoneNumber phone, DateTimeOffset now, CancellationToken ct)
    {
        if (message.Kind == InboundMessageKind.Location && message.Location is { } loc)
        {
            draft.CaptureLocation(loc.Latitude, loc.Longitude, loc.Address ?? loc.Name ?? "(GPS pin)", now);
            draft.StepTo(ClientRequestStep.AwaitingDescription, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Got your location. Want to add a short description? Send it now or reply SKIP."));
            return;
        }

        if (message.Kind == InboundMessageKind.Text && !string.IsNullOrWhiteSpace(message.Text))
        {
            // Defer geocoding HTTP off the inbound critical path (~10s Google timeout).
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "Looking up that address — one sec…"));
            await bus.PublishAsync(new GeocodeAddressRequested(
                phone.Value, message.Text!, GeocodeFlow.Client));
            return;
        }

        await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Send your location pin or type your address."));
    }

    private async Task ConfirmLocationAsync(ClientRequestDraft draft, InboundMessage message, PhoneNumber phone, DateTimeOffset now, CancellationToken ct)
    {
        if (message.Kind == InboundMessageKind.Location && message.Location is { } loc)
        {
            draft.CaptureLocation(loc.Latitude, loc.Longitude, loc.Address ?? loc.Name ?? "(GPS pin)", now);
            draft.StepTo(ClientRequestStep.AwaitingDescription, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Got your location. Want to add a description? Send it now or reply SKIP."));
            return;
        }
        if (QuickIntent.Detect(message.Text) == IntentKind.Confirmation)
        {
            draft.StepTo(ClientRequestStep.AwaitingDescription, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Want to add a description? Send it now or reply SKIP."));
            return;
        }
        await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Reply YES to confirm or send your GPS pin."));
    }

    private async Task CollectDescriptionAsync(ClientRequestDraft draft, InboundMessage message, PhoneNumber phone, DateTimeOffset now, CancellationToken ct)
    {
        var text = message.Text?.Trim();
        if (!IsSkipDescription(text))
        {
            draft.CaptureDescription(text, now);
        }

        if (string.IsNullOrEmpty(draft.DraftServiceSlug) || draft.DraftLatitude is null || draft.DraftLongitude is null)
        {
            logger.LogWarning("Incomplete client draft for {Phone}", phone.Mask());
            await drafts.DeleteAsync(phone.Value, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "Couldn't save your request — I'm missing the service or your location. Reply with what you need (e.g. \"I need a plumber\") and send a location pin."));
            return;
        }

        // Reject same-service dual role: a listed plumbing provider cannot
        // request plumbing from themselves. Different services (e.g. a plumber
        // requesting carpentry) are allowed silently.
        var existingAvailability = await availability.GetAsync(phone.Value, ct);
        if (existingAvailability is not null &&
            existingAvailability.Services.Contains(draft.DraftServiceSlug))
        {
            var human = draft.DraftServiceSlug.Replace('-', ' ');
            logger.LogDebug("Rejecting same-service dual-role request for {Phone} slug={Slug}",
                phone.Mask(), draft.DraftServiceSlug);
            await drafts.DeleteAsync(phone.Value, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                $"You can't request a service you're already listed to provide. To request {human}, first reply LEAVE to unlist from {human} (your other services stay active), then send your request again."));
            return;
        }

        // Description captured + guards passed. Park the draft awaiting the requester's
        // phone-share decision; the request itself is created in CollectPhoneShareConsentAsync
        // so SharePhoneNumber is set atomically with creation.
        draft.StepTo(ClientRequestStep.AwaitingPhoneShareConsent, now);
        await drafts.UpsertAsync(draft, ct);
        await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
            "One more thing — should we share your phone number with selected providers? Reply YES or NO."));
    }

    private async Task CollectPhoneShareConsentAsync(ClientRequestDraft draft, InboundMessage message, PhoneNumber phone, DateTimeOffset now, CancellationToken ct)
    {
        var quick = QuickIntent.Detect(message.Text);
        if (quick != IntentKind.Confirmation && quick != IntentKind.Rejection)
        {
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "Should we share your phone number with selected providers? Reply YES or NO."));
            return;
        }

        var consent = quick == IntentKind.Confirmation;
        draft.SetPhoneShareConsent(consent, now);

        if (string.IsNullOrEmpty(draft.DraftServiceSlug) || draft.DraftLatitude is null || draft.DraftLongitude is null)
        {
            logger.LogWarning("Consent received with incomplete draft for {Phone}", phone.Mask());
            await drafts.DeleteAsync(phone.Value, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "Couldn't save your request — I'm missing the service or your location. Reply with what you need (e.g. \"I need a plumber\") and send a location pin."));
            return;
        }

        try
        {
            var request = RequestAggregate.ServiceRequest.Create(
                phone.Value,
                draft.DraftServiceSlug,
                new Location(draft.DraftLatitude.Value, draft.DraftLongitude.Value),
                draft.DraftFormattedAddress,
                draft.DraftDescription,
                matchingOptions.Value.DefaultRadiusKm,
                now,
                sharePhoneNumber: consent);

            await requests.AddAsync(request, ct);
            await drafts.DeleteAsync(phone.Value, ct);

            await bus.PublishAsync(new SendWhatsAppTextRequested(phone, "Looking for nearby providers…"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to finalize client request for {Phone}", phone.Mask());
            await drafts.DeleteAsync(phone.Value, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "Something went wrong saving your request. Try again in a moment — reply with what you need (e.g. \"I need a plumber\")."));
        }
    }

    private static bool IsSkipDescription([System.Diagnostics.CodeAnalysis.NotNullWhen(false)] string? text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        if (string.Equals(text, "SKIP", StringComparison.OrdinalIgnoreCase)) return true;
        if (QuickIntent.Detect(text) is IntentKind.Confirmation
                                      or IntentKind.Rejection
                                      or IntentKind.Cancel) return true;
        var s = text.Trim().ToLowerInvariant();
        return s is "no description" or "no desc" or "nothing"
                 or "skip it" or "i'm good" or "im good" or "all good"
                 or "continue" or "go ahead" or "proceed"
                 or "no thanks" or "that's all" or "thats all";
    }
}
