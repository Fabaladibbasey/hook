namespace Hook.Features.ContactSharing.Events;

public sealed record ContactExchanged(Guid MatchId, Guid RequestId, string ClientPhone, string ProviderPhone);

public sealed record ChatRoutingRequested(
    Guid MatchId,
    Guid RequestId,
    string ClientPhone,
    string ProviderPhone,
    bool ClientConsented,
    bool ProviderConsented,
    string RequesterAddress,
    double RequesterLatitude,
    double RequesterLongitude,
    int MatchPosition,
    string Description = "");
