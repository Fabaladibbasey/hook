using Hook.Features.ContactSharing.Events;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Wolverine;

namespace Hook.Features.ContactSharing.ExchangePhones;

public sealed class PhoneExchanger(
    IMatchRepository matches,
    IServiceRequestRepository requests,
    IProviderAvailabilityRepository providers,
    IWhatsappClient whatsapp,
    IMessageBus bus,
    ILogger<PhoneExchanger> logger)
{
    public async Task<bool> TryExchangeAsync(Guid matchId, CancellationToken ct = default)
    {
        var match = await matches.GetAsync(matchId, ct);
        if (match is null)
        {
            logger.LogWarning("Match {MatchId} not found", matchId);
            return false;
        }

        var request = await requests.GetAsync(match.RequestId, ct);
        var provider = await providers.GetAsync(match.ProviderPhone, ct);
        if (request is null || provider is null) return false;

        if (!provider.ShareContact)
        {
            await bus.PublishAsync(new ChatRoutingRequested(match.Id, request.Id, request.ClientPhone, provider.Phone));
            return false;
        }

        if (!PhoneNumber.TryParse(request.ClientPhone, out var clientPhone) ||
            !PhoneNumber.TryParse(provider.Phone, out var providerPhone))
        {
            return false;
        }

        await whatsapp.SendTextAsync(clientPhone,
            $"Provider for {match.ServiceSlug}: {providerPhone.Value}. Reach out directly.", ct);

        if (!match.ContactShared)
        {
            match.ContactShared = true;
            await matches.SaveChangesAsync(ct);
            await bus.PublishAsync(new ContactExchanged(match.Id, request.Id, request.ClientPhone, provider.Phone));
        }

        return true;
    }
}
