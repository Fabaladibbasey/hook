using Hook.Shared.Domain;

namespace Hook.Features.ContactSharing.Events;

public sealed record ContactExchangedEvent(Guid MatchId, Guid RequestId, string ClientPhone, string ProviderPhone) : IDomainEvent;
