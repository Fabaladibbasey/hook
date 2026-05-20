using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.Geocoding.Geocode;

public sealed class GeocodeAddressDispatchHandler(
    GeocodingService geocoding,
    ILogger<GeocodeAddressDispatchHandler> logger)
{
    // [NonTransactional]: Google Geocoding HTTP can wait up to ~10s (its default client
    // timeout). bus.InvokeAsync re-enters a transactional apply handler so the draft
    // mutation + outgoing prompt commit atomically with the outbox envelope.
    [NonTransactional]
    public async Task Handle(GeocodeAddressRequested evt, IMessageBus bus, CancellationToken ct)
    {
        if (!PhoneNumber.TryParse(evt.Phone, out var phone))
        {
            logger.LogWarning("GeocodeAddressRequested with unparseable phone");
            return;
        }

        var result = await geocoding.GeocodeAsync(evt.AddressText, ct);
        if (result is null)
        {
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                "I couldn't find that address. Try typing it differently, or send a GPS pin (📎 → Location)."));
            return;
        }

        switch (evt.Flow)
        {
            case GeocodeFlow.Client:
                await bus.InvokeAsync(new ApplyGeocodeResultClient(evt.Phone, result), ct);
                return;
            case GeocodeFlow.Provider:
                await bus.InvokeAsync(new ApplyGeocodeResultProvider(evt.Phone, result), ct);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(evt), evt.Flow, "Unknown GeocodeFlow");
        }
    }
}
