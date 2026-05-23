using Hook.Features.ContactSharing.Events;
using Hook.Shared.Domain;

namespace Hook.Features.Matching.MatchAggregate;

public class Match : AggregateRoot
{
    public Guid Id { get; init; }
    public required Guid RequestId { get; init; }
    public required string ProviderPhone { get; init; }
    public required string ServiceSlug { get; init; }
    public double DistanceKm { get; init; }
    public double Score { get; init; }
    public MatchKind Kind { get; init; } = MatchKind.Exact;
    public bool ContactShared { get; private set; }
    public Guid? ChatId { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? PickedAt { get; private set; }

    public static Match Create(
        Guid requestId,
        string providerPhone,
        string serviceSlug,
        double distanceKm,
        double score,
        DateTimeOffset now,
        MatchKind kind = MatchKind.Exact) => new()
        {
            Id = Guid.CreateVersion7(),
            RequestId = requestId,
            ProviderPhone = providerPhone,
            ServiceSlug = serviceSlug,
            DistanceKm = distanceKm,
            Score = score,
            Kind = kind,
            CreatedAt = now
        };

    public void ClaimForPickup(bool contactShared, DateTimeOffset now)
    {
        if (PickedAt is not null)
            throw new InvalidOperationException($"Match {Id} already picked at {PickedAt}");
        PickedAt = now;
        ContactShared = contactShared;
    }

    public void ClaimForChat(Guid chatId)
    {
        if (ChatId is not null)
            throw new InvalidOperationException($"Match {Id} already routed to chat {ChatId}");
        ChatId = chatId;
    }

    public void MarkContactExchanged(Guid requestId, string clientPhone, string providerPhone)
    {
        if (ContactShared)
            throw new InvalidOperationException($"Match {Id} contacts already exchanged");
        RaiseDomainEvent(new ContactExchangedEvent(Id, requestId, clientPhone, providerPhone));
    }
}
