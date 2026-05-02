namespace Hook.Features.ContactSharing.Events;

public sealed record ContactExchanged(Guid MatchId, Guid RequestId, string ClientPhone, string ProviderPhone);

public sealed record ChatRoutingRequested(Guid MatchId, Guid RequestId, string ClientPhone, string ProviderPhone);
