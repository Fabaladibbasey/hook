using Hook.Features.Geocoding.Models;
using Hook.Features.ServiceRequest.Create;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;

namespace Hook.Features.Geocoding.Geocode;

public sealed record ApplyGeocodeResultClient(
    string Phone,
    GeocodeResult Result,
    DateTimeOffset DraftStampedAt = default,
    string Reserved = "");

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

        // Stamp guard: a CANCEL+restart sequence can land the user back at
        // AwaitingLocation on a NEW draft while the original Google round-trip
        // is still in flight; without this the stale coordinates apply to the
        // new draft. evt.DraftStampedAt = draft.UpdatedAt at publish time.
        // 1µs tolerance absorbs Postgres timestamptz truncation; the CANCEL+restart
        // race is many seconds wide so the tolerance does not collapse the guard.
        if (evt.DraftStampedAt != default
            && Math.Abs((draft.UpdatedAt - evt.DraftStampedAt).Ticks) > 10)
        {
            logger.LogDebug("Stale geocode apply for {Phone}; draft restamped at {Now} vs envelope {Then}",
                phone.Mask(), draft.UpdatedAt, evt.DraftStampedAt);
            return;
        }

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
