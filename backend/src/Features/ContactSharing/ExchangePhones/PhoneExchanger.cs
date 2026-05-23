using Hook.Features.ContactSharing.Events;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Core;
using Hook.Shared.Pipeline.PostCommitSends;
using Hook.Shared.Whatsapp;
using Wolverine;

namespace Hook.Features.ContactSharing.ExchangePhones;

public sealed class PhoneExchanger(
    IMatchRepository matches,
    IServiceRequestRepository requests,
    IProviderAvailabilityRepository providers,
    IMessageBus bus,
    IEventPublisher events,
    TimeProvider clock,
    ILogger<PhoneExchanger> logger)
{
    public async Task<ExchangeOutcome> TryExchangeAsync(Guid matchId, int matchPosition, CancellationToken ct = default)
    {
        var match = await matches.GetAsync(matchId, ct);
        if (match is null)
        {
            logger.LogWarning("Match {MatchId} not found", matchId);
            return ExchangeOutcome.InvalidData;
        }

        var request = await requests.GetAsync(match.RequestId, ct);
        if (request is null) return ExchangeOutcome.RequestMissing;

        var provider = await providers.GetAsync(match.ProviderPhone, ct);
        if (provider is null) return ExchangeOutcome.ProviderMissing;

        var now = clock.GetUtcNow();
        if (provider.ExpiresAt <= now) return ExchangeOutcome.ProviderExpired;

        if (!PhoneNumber.TryParse(request.ClientPhone, out var clientPhone))
        {
            logger.LogWarning("Match {MatchId} request has unparseable client phone", matchId);
            return ExchangeOutcome.InvalidData;
        }
        if (!PhoneNumber.TryParse(provider.Phone, out var providerPhone))
        {
            logger.LogError("Match {MatchId} provider {ProviderPhone} has unparseable phone — data integrity issue",
                matchId, provider.Phone);
            return ExchangeOutcome.InvalidData;
        }

        if (match.ContactShared)
        {
            await bus.PublishAsync(new SendWhatsAppTextRequested(clientPhone,
                FormatProviderResend(matchPosition, match.ServiceSlug, providerPhone)));
            return ExchangeOutcome.AlreadyShared;
        }

        if (match.PickedAt is not null)
        {
            // Re-pick of a chat-routed match: ChatRoutingRequestedHandler is idempotent
            // on ChatId, so re-publishing wouldn't re-deliver the link. Send a brief
            // notice pointing the client at the existing chat thread instead.
            var text =
                $"Match #{matchPosition} ({providerPhone.Mask()}): " +
                $"already connected to chat with the provider for {match.ServiceSlug}. " +
                "Check your earlier messages for the chat link.";
            await bus.PublishAsync(new SendWhatsAppTextRequested(clientPhone, text));
            return ExchangeOutcome.AlreadyRouted;
        }

        var bothConsent = request.SharePhoneNumber && provider.ShareContact;
        var claim = new PickClaim(matchId, request.ClientPhone, bothConsent, now);
        if (!await matches.TryClaimPickAsync(claim, ct))
        {
            // Atomic claim folds the consent + TTL re-check, so this also fires when a
            // provider revokes ShareContact between our read and the UPDATE — closing
            // the TOCTOU window that would otherwise leak phones.
            return ExchangeOutcome.RaceLost;
        }

        if (!bothConsent)
        {
            await events.PublishAsync(new ChatRoutingRequested(
                match.Id,
                request.Id,
                request.ClientPhone,
                provider.Phone,
                request.SharePhoneNumber,
                provider.ShareContact,
                request.FormattedAddress,
                request.Location.Y,
                request.Location.X,
                matchPosition,
                request.Description), ct);
            return ExchangeOutcome.RoutedToChat;
        }

        await bus.PublishAsync(new SendWhatsAppTextRequested(clientPhone,
            FormatProviderResend(matchPosition, match.ServiceSlug, providerPhone)));
        var providerText = RequestDetailsFormatter.AppendIfPresent(
            $"Client wants {match.ServiceSlug} ({clientPhone.Value}). Expect a message.",
            request.Description);
        await bus.PublishAsync(new SendWhatsAppTextRequested(providerPhone, providerText));
        await events.PublishAsync(new ContactExchanged(match.Id, request.Id, request.ClientPhone, provider.Phone), ct);
        return ExchangeOutcome.Exchanged;
    }

    private static string FormatProviderResend(int position, string serviceSlug, PhoneNumber providerPhone) =>
        $"Match #{position}: provider for {serviceSlug}: {providerPhone.Value}. Reach out directly.";
}
