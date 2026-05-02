using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Geocoding.Geocode;
using Hook.Features.Matching;
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
                await CollectDescriptionAsync(draft, message, phone, now, ct);
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
            await whatsapp.SendTextAsync(phone, "What service do you need? (e.g. plumber, carpenter, computer repair)", ct);
            await drafts.UpsertAsync(draft, ct);
            return;
        }

        var canonical = await slugResolver.ResolveAsync(extracted.Slugs[0], text, ct);
        draft.DraftServiceSlug = canonical.CanonicalSlug;
        draft.Step = ClientRequestStep.ConfirmService;
        await drafts.UpsertAsync(draft, ct);
        await whatsapp.SendTextAsync(phone, $"Do you need {canonical.CanonicalSlug.Replace('-', ' ')}? Reply YES or NO.", ct);
    }

    private async Task ConfirmServiceAsync(ClientRequestDraft draft, InboundMessage message, PhoneNumber phone, CancellationToken ct)
    {
        var quick = QuickIntent.Detect(message.Text);
        var intent = quick is { } q
            ? new IntentDetectionResult(q, 1.0, "en", "quick")
            : await ai.DetectIntentAsync(message.Text ?? string.Empty, ct);
        if (intent.Intent == IntentKind.Confirmation)
        {
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
            await whatsapp.SendTextAsync(phone, "What service do you need?", ct);
            return;
        }
        await whatsapp.SendTextAsync(phone, "Reply YES or NO.", ct);
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

    private async Task CollectDescriptionAsync(ClientRequestDraft draft, InboundMessage message, PhoneNumber phone, DateTimeOffset now, CancellationToken ct)
    {
        var text = message.Text?.Trim();
        if (!string.IsNullOrEmpty(text) && !string.Equals(text, "SKIP", StringComparison.OrdinalIgnoreCase))
        {
            draft.DraftDescription = text;
        }

        if (string.IsNullOrEmpty(draft.DraftServiceSlug) || draft.DraftLatitude is null || draft.DraftLongitude is null)
        {
            logger.LogWarning("Incomplete client draft for {Phone}", phone.Mask());
            await drafts.DeleteAsync(phone.Value, ct);
            return;
        }

        var request = RequestAggregate.ServiceRequest.Create(
            phone.Value,
            draft.DraftServiceSlug,
            new Location(draft.DraftLatitude.Value, draft.DraftLongitude.Value),
            draft.DraftFormattedAddress,
            draft.DraftDescription,
            matchingOptions.Value.DefaultRadiusKm,
            now);

        await requests.AddAsync(request, ct);
        await requests.SaveChangesAsync(ct);
        await drafts.DeleteAsync(phone.Value, ct);

        await whatsapp.SendTextAsync(phone, "Looking for nearby providers…", ct);
        await bus.PublishAsync(new ServiceRequestCreated(request.Id));
    }
}
