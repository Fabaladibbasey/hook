using Hook.Shared.Domain;

namespace Hook.Features.ContactSharing.Events;

public sealed record ContactExchangedEvent(Guid MatchId) : IDomainEvent;
