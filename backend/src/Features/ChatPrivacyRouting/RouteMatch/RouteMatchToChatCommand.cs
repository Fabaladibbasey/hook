namespace Hook.Features.ChatPrivacyRouting.RouteMatch;

public sealed record RouteMatchToChatCommand(
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
