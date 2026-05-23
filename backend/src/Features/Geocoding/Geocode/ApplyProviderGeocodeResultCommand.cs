using Hook.Features.Geocoding.Models;
using Hook.Features.ProviderAvailability.Register;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;

namespace Hook.Features.Geocoding.Geocode;

public sealed record ApplyProviderGeocodeResultCommand(
    string Phone,
    GeocodeResult Result,
    DateTimeOffset DraftStampedAt = default,
    string Reserved = "");

public sealed class ApplyProviderGeocodeResultHandler(
    IRegistrationDraftRepository drafts,
    TimeProvider clock,
    ILogger<ApplyProviderGeocodeResultHandler> logger)
{
    public async Task Handle(ApplyProviderGeocodeResultCommand evt, IMessageBus bus, CancellationToken ct)
    {
        var draft = await drafts.GetAsync(evt.Phone, ct);
        if (draft is null) return;

        if (!PhoneNumber.TryParse(evt.Phone, out var phone)) return;

        if (GeocodeStampGuard.IsStale(draft.UpdatedAt, evt.DraftStampedAt))
        {
            logger.LogDebug("Stale geocode apply for {Phone}; draft restamped at {Now} vs envelope {Then}",
                phone.Mask(), draft.UpdatedAt, evt.DraftStampedAt);
            return;
        }

        if (draft.Step is not (RegistrationStep.AwaitingLocation or RegistrationStep.ConfirmLocation))
        {
            logger.LogDebug("Stale geocode apply for {Phone}; draft now at {Step}", phone.Mask(), draft.Step);
            return;
        }

        var now = clock.GetUtcNow();
        draft.CaptureLocation(
            evt.Result.Location.Latitude,
            evt.Result.Location.Longitude,
            evt.Result.FormattedAddress,
            now);
        draft.StepTo(RegistrationStep.ConfirmLocation, now);
        await drafts.UpsertAsync(draft, ct);
        await bus.PublishAsync(new SendWhatsAppTextCommand(phone,
            $"Found: '{evt.Result.FormattedAddress}'. Reply YES to confirm or send your GPS pin instead."));
    }
}
