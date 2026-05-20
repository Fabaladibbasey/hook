using Hook.Features.Geocoding.Models;
using Hook.Features.ServiceRequest.Create;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;

namespace Hook.Features.Geocoding.Geocode;

public sealed record ApplyGeocodeResultClient(string Phone, GeocodeResult Result, string Reserved = "");

public sealed class ApplyGeocodeResultClientHandler(
    IClientRequestDraftRepository drafts,
    TimeProvider clock,
    ILogger<ApplyGeocodeResultClientHandler> logger)
{
    public async Task Handle(ApplyGeocodeResultClient evt, IMessageBus bus, CancellationToken ct)
    {
        var draft = await drafts.GetAsync(evt.Phone, ct);
        if (draft is null) return;

        if (!PhoneNumber.TryParse(evt.Phone, out var phone)) return;

        // Race guard: user may have advanced past the location step (e.g. sent a GPS pin)
        // while the geocoder was running. Only act if we're still in a location step.
        if (draft.Step is not (ClientRequestStep.AwaitingLocation or ClientRequestStep.ConfirmLocation))
        {
            logger.LogDebug("Stale geocode apply for {Phone}; draft now at {Step}", phone.Mask(), draft.Step);
            return;
        }

        var now = clock.GetUtcNow();
        draft.CaptureLocation(evt.Result.Location.Latitude, evt.Result.Location.Longitude, evt.Result.FormattedAddress, now);
        draft.StepTo(ClientRequestStep.ConfirmLocation, now);
        await drafts.UpsertAsync(draft, ct);
        await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
            $"Found: '{evt.Result.FormattedAddress}'. Reply YES to confirm or send your GPS pin."));
    }
}
