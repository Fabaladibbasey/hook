using Hook.Features.ContactSharing.Events;
using Hook.Shared.Domain;

namespace Hook.Features.Matching.MatchAggregate;

public class Match : AggregateRoot
{
    public Guid Id { get; private init; }
    public Guid RequestId { get; private init; }
    public string ProviderPhone { get; private init; } = string.Empty;
    public string ServiceSlug { get; private init; } = string.Empty;
    public double DistanceKm { get; private init; }
    public double Score { get; private init; }
    public MatchKind Kind { get; private init; } = MatchKind.Exact;
    public bool ContactShared { get; private set; }
    public Guid? ChatId { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset? PickedAt { get; private set; }

    public static Match Create(
        Guid requestId,
        string providerPhone,
        string serviceSlug,
        double distanceKm,
        double score,
        DateTimeOffset now,
        MatchKind kind = MatchKind.Exact) =>
        CreateWithId(Guid.CreateVersion7(), requestId, providerPhone, serviceSlug, distanceKm, score, now, kind);

    // Deterministic-id factory exposed to tests only (InternalsVisibleTo Hook.IntegrationTests
    // in Hook.csproj). Production callers must use the public Create overload — the public
    // surface is one method that auto-generates a Version7 id.
    internal static Match CreateWithId(
        Guid id,
        Guid requestId,
        string providerPhone,
        string serviceSlug,
        double distanceKm,
        double score,
        DateTimeOffset now,
        MatchKind kind = MatchKind.Exact) => new()
        {
            Id = id,
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

    public void MarkContactExchanged()
    {
        if (PickedAt is null)
            throw new InvalidOperationException($"Match {Id} contact-exchange before claim");
        RaiseDomainEvent(new ContactExchangedEvent(Id));
    }
}
