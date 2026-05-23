using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.Geocoding.Geocode;

public sealed class GeocodeAddressDispatchHandler(
    GeocodingService geocoding,
    ILogger<GeocodeAddressDispatchHandler> logger)
{
    // [NonTransactional]: Google Geocoding HTTP can wait ~10s; do not pin an Npgsql tx
    // across it. bus.PublishAsync enqueues the apply-command to the durable outbox.
    [NonTransactional]
    public async Task Handle(GeocodeAddressCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        if (!PhoneNumber.TryParse(cmd.Phone, out var phone))
        {
            logger.LogWarning("GeocodeAddressCommand with unparseable phone");
            return;
        }

        var result = await geocoding.GeocodeAsync(cmd.AddressText, ct);
        if (result is null)
        {
            await bus.PublishAsync(new SendWhatsAppTextCommand(phone,
                "I couldn't find that address. Try typing it differently, or send a GPS pin (📎 → Location)."));
            return;
        }

        switch (cmd.Flow)
        {
            case GeocodeFlow.Client:
                await bus.PublishAsync(new ApplyClientGeocodeResultCommand(cmd.Phone, result, cmd.DraftStampedAt));
                return;
            case GeocodeFlow.Provider:
                await bus.PublishAsync(new ApplyProviderGeocodeResultCommand(cmd.Phone, result, cmd.DraftStampedAt));
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(cmd), cmd.Flow, "Unknown GeocodeFlow");
        }
    }
}
