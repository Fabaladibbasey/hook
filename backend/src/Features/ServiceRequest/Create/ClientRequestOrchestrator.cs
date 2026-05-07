using System.Text.RegularExpressions;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Geocoding.Geocode;
using Hook.Features.Matching;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.ServiceTaxonomy.ResolveSlug;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Options;
using Wolverine;
using Location = Hook.Features.Geocoding.Models.Location;

namespace Hook.Features.ServiceRequest.Create;

public sealed class ClientRequestOrchestrator(
    IClientRequestDraftRepository drafts,
    IServiceRequestRepository requests,
    IProviderAvailabilityRepository availability,
    IConversationAi ai,
    SlugResolver slugResolver,
    GeocodingService geocoding,
    IWhatsappClient whatsapp,
    IMessageBus bus,
    IOptions<MatchingOptions> matchingOptions,
    TimeProvider clock,
    ILogger<ClientRequestOrchestrator> logger)
{
    public async Task HandleAsync(InboundMessage message, CancellationToken ct = default)
    {
        var phone = message.From;
        var now = clock.GetUtcNow();
        var draft = await drafts.GetAsync(phone.Value, ct) ?? ClientRequestDraft.Start(phone.Value, now);
        draft.UpdatedAt = now;

        // Slug switch mid-funnel: if the user is past the slug-confirm step and sends a
        // strong service-request hint with a different canonical slug, reset to
        // ConfirmService for the new slug while preserving any captured location. The
        // hint guard avoids running an LLM extract on every location/description reply.
        if (draft.Step is ClientRequestStep.AwaitingLocation
                       or ClientRequestStep.ConfirmLocation
                       or ClientRequestStep.AwaitingDescription
            && QuickIntent.DetectIntentHint(message.Text) == IntentKind.ServiceRequest)
        {
            var extracted = await ai.ExtractServicesAsync(message.Text ?? string.Empty, ct);
            if (extracted.Slugs.Count > 0)
            {
                var canonical = await slugResolver.ResolveAsync(extracted.Slugs[0], message.Text ?? string.Empty, ct);
                if (!string.Equals(canonical.CanonicalSlug, draft.DraftServiceSlug, StringComparison.Ordinal))
                {
                    draft.DraftServiceSlug = canonical.CanonicalSlug;
                    draft.Step = ClientRequestStep.ConfirmService;
                    // Location fields preserved on draft so YES skips straight to AwaitingDescription.
                    await drafts.UpsertAsync(draft, ct);
                    logger.LogDebug("Slug switch → {Slug} for {Phone}", canonical.CanonicalSlug, phone.Mask());
                    await whatsapp.SendTextAsync(phone,
                        $"Switching to {canonical.CanonicalSlug.Replace('-', ' ')}. Reply YES to confirm or NO to choose another.", ct);
                    return;
                }
            }
        }

        switch (draft.Step)
        {
            case ClientRequestStep.AwaitingService:
                await StartAsync(draft, message, phone, ct);
                break;
            case ClientRequestStep.ConfirmService:
                await ConfirmServiceAsync(draft, message, phone, ct);
                break;
            case ClientRequestStep.AwaitingLocation:
                await CollectLocationAsync(draft, message, phone, ct);
                break;
            case ClientRequestStep.ConfirmLocation:
                await ConfirmLocationAsync(draft, message, phone, ct);
                break;
            case ClientRequestStep.AwaitingDescription:
                await CollectDescriptionAsync(draft, message, phone, ct);
                break;
            case ClientRequestStep.AwaitingPhoneShareConsent:
                await CollectPhoneShareConsentAsync(draft, message, phone, now, ct);
                break;
            default:
                logger.LogWarning("Unexpected draft step {Step} for {Phone}", draft.Step, phone.Mask());
                break;
        }
    }

    private async Task StartAsync(ClientRequestDraft draft, InboundMessage message, PhoneNumber phone, CancellationToken ct)
    {
        var text = message.Text ?? string.Empty;
        var extracted = await ai.ExtractServicesAsync(text, ct);
        if (extracted.Slugs.Count == 0)
        {
            // Don't assume the user wants to REQUEST — they may have meant to REGISTER
            // and only landed here because the LLM-classified intent edged over the
            // confidence threshold. The REGISTER hint keeps the door open without
            // restarting the conversation.
            await whatsapp.SendTextAsync(phone,
                "What service do you need? (e.g. plumber, carpenter, computer repair) — or reply REGISTER if you're offering services instead.", ct);
            await drafts.UpsertAsync(draft, ct);
            return;
        }

        var canonical = await slugResolver.ResolveAsync(extracted.Slugs[0], text, ct);
        draft.DraftServiceSlug = canonical.CanonicalSlug;
        draft.Step = ClientRequestStep.ConfirmService;
        await drafts.UpsertAsync(draft, ct);
        await whatsapp.SendTextAsync(phone, $"Do you need {canonical.CanonicalSlug.Replace('-', ' ')}? Reply YES or NO — YES to confirm, NO to choose another service.", ct);
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

    private async Task ConfirmServiceAsync(ClientRequestDraft draft, InboundMessage message, PhoneNumber phone, CancellationToken ct)
    {
        var leadingNo = LeadingNoRx.Match(message.Text ?? string.Empty);
        if (leadingNo.Success)
        {
            var rest = leadingNo.Groups["rest"].Value.Trim();
            if (rest.Length > 0 && QuickIntent.Detect(rest) is null)
            {
                draft.DraftServiceSlug = string.Empty;
                draft.Step = ClientRequestStep.AwaitingService;
                var replay = message with { Text = rest };
                await StartAsync(draft, replay, phone, ct);
                return;
            }
        }

        var quick = QuickIntent.Detect(message.Text);
        var intent = quick is { } q
            ? new IntentDetectionResult(q, 1.0, "en", "quick")
            : await ai.DetectIntentAsync(message.Text ?? string.Empty, ct);
        if (intent.Intent == IntentKind.Confirmation)
        {
            // If we already have a captured location (slug-switch path), skip the
            // location collection steps and jump straight to description.
            if (draft.DraftLatitude is not null && draft.DraftLongitude is not null)
            {
                draft.Step = ClientRequestStep.AwaitingDescription;
                await drafts.UpsertAsync(draft, ct);
                await whatsapp.SendTextAsync(phone,
                    $"Got it. Using your saved location: {draft.DraftFormattedAddress}. Want to add a description? Send it now or reply SKIP.", ct);
                return;
            }

            draft.Step = ClientRequestStep.AwaitingLocation;
            await drafts.UpsertAsync(draft, ct);
            await whatsapp.SendTextAsync(phone, "Send your location pin (or type your address).", ct);
            return;
        }
        if (intent.Intent == IntentKind.Rejection)
        {
            draft.DraftServiceSlug = string.Empty;
            draft.Step = ClientRequestStep.AwaitingService;
            await drafts.UpsertAsync(draft, ct);
            await whatsapp.SendTextAsync(phone,
                "What service do you need? Or reply REGISTER if you're offering services instead.", ct);
            return;
        }
        await whatsapp.SendTextAsync(phone, $"Please reply YES or NO — YES to confirm {draft.DraftServiceSlug.Replace('-', ' ')}, NO to choose another service.", ct);
    }

    private async Task CollectLocationAsync(ClientRequestDraft draft, InboundMessage message, PhoneNumber phone, CancellationToken ct)
    {
        if (message.Kind == InboundMessageKind.Location && message.Location is { } loc)
        {
            draft.DraftLatitude = loc.Latitude;
            draft.DraftLongitude = loc.Longitude;
            draft.DraftFormattedAddress = loc.Address ?? loc.Name ?? "(GPS pin)";
            draft.Step = ClientRequestStep.AwaitingDescription;
            await drafts.UpsertAsync(draft, ct);
            await whatsapp.SendTextAsync(phone, "Got your location. Want to add a short description? Send it now or reply SKIP.", ct);
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
            draft.Step = ClientRequestStep.ConfirmLocation;
            await drafts.UpsertAsync(draft, ct);
            await whatsapp.SendTextAsync(phone, $"Found: '{geocoded.FormattedAddress}'. Reply YES to confirm or send your GPS pin.", ct);
            return;
        }

        await whatsapp.SendTextAsync(phone, "Send your location pin or type your address.", ct);
    }

    private async Task ConfirmLocationAsync(ClientRequestDraft draft, InboundMessage message, PhoneNumber phone, CancellationToken ct)
    {
        if (message.Kind == InboundMessageKind.Location && message.Location is { } loc)
        {
            draft.DraftLatitude = loc.Latitude;
            draft.DraftLongitude = loc.Longitude;
            draft.DraftFormattedAddress = loc.Address ?? loc.Name ?? "(GPS pin)";
            draft.Step = ClientRequestStep.AwaitingDescription;
            await drafts.UpsertAsync(draft, ct);
            await whatsapp.SendTextAsync(phone, "Got your location. Want to add a description? Send it now or reply SKIP.", ct);
            return;
        }
        var quick = QuickIntent.Detect(message.Text);
        var intent = quick is { } q
            ? new IntentDetectionResult(q, 1.0, "en", "quick")
            : await ai.DetectIntentAsync(message.Text ?? string.Empty, ct);
        if (intent.Intent == IntentKind.Confirmation)
        {
            draft.Step = ClientRequestStep.AwaitingDescription;
            await drafts.UpsertAsync(draft, ct);
            await whatsapp.SendTextAsync(phone, "Want to add a description? Send it now or reply SKIP.", ct);
            return;
        }
        await whatsapp.SendTextAsync(phone, "Reply YES to confirm or send your GPS pin.", ct);
    }

    private async Task CollectDescriptionAsync(ClientRequestDraft draft, InboundMessage message, PhoneNumber phone, CancellationToken ct)
    {
        var text = message.Text?.Trim();
        if (!IsSkipDescription(text))
        {
            draft.DraftDescription = text;
        }

        if (string.IsNullOrEmpty(draft.DraftServiceSlug) || draft.DraftLatitude is null || draft.DraftLongitude is null)
        {
            logger.LogWarning("Incomplete client draft for {Phone}", phone.Mask());
            await drafts.DeleteAsync(phone.Value, ct);
            await whatsapp.SendTextAsync(phone,
                "Couldn't save your request — some details were missing. Reply with what service you need to start over.", ct);
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
            await whatsapp.SendTextAsync(phone,
                $"You're listed as a {human} provider. To request {human} instead, reply LEAVE to unlist first, then send your request again.", ct);
            return;
        }

        // Description captured + guards passed. Park the draft awaiting the requester's
        // phone-share decision; the request itself is created in CollectPhoneShareConsentAsync
        // so SharePhoneNumber is set atomically with creation.
        draft.Step = ClientRequestStep.AwaitingPhoneShareConsent;
        await drafts.UpsertAsync(draft, ct);
        await whatsapp.SendTextAsync(phone,
            "One more thing — should we share your phone number with selected providers? Reply YES or NO.", ct);
    }

    private async Task CollectPhoneShareConsentAsync(ClientRequestDraft draft, InboundMessage message, PhoneNumber phone, DateTimeOffset now, CancellationToken ct)
    {
        var quick = QuickIntent.Detect(message.Text);
        var intent = quick is { } q
            ? new IntentDetectionResult(q, 1.0, "en", "quick")
            : await ai.DetectIntentAsync(message.Text ?? string.Empty, ct);

        if (intent.Intent != IntentKind.Confirmation && intent.Intent != IntentKind.Rejection)
        {
            await whatsapp.SendTextAsync(phone,
                "Should we share your phone number with selected providers? Reply YES or NO.", ct);
            return;
        }

        var consent = intent.Intent == IntentKind.Confirmation;
        draft.DraftSharePhoneConsent = consent;

        if (string.IsNullOrEmpty(draft.DraftServiceSlug) || draft.DraftLatitude is null || draft.DraftLongitude is null)
        {
            logger.LogWarning("Consent received with incomplete draft for {Phone}", phone.Mask());
            await drafts.DeleteAsync(phone.Value, ct);
            await whatsapp.SendTextAsync(phone,
                "Couldn't save your request — some details were missing. Reply with what service you need to start over.", ct);
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
            await requests.SaveChangesAsync(ct);
            await drafts.DeleteAsync(phone.Value, ct);

            await whatsapp.SendTextAsync(phone, "Looking for nearby providers…", ct);
            await bus.PublishAsync(new ServiceRequestCreated(request.Id));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to finalize client request for {Phone}", phone.Mask());
            await drafts.DeleteAsync(phone.Value, ct);
            await whatsapp.SendTextAsync(phone,
                "Couldn't save your request right now. Reply with what service you need (e.g. 'I need a plumber') to try again.", ct);
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
