namespace Hook.Features.ContactSharing.ExchangePhones;

// Outcomes split by terminal vs transient failure mode so callers can decide whether
// to retry, abandon, or notify the user differently. AlreadyShared/AlreadyRouted are
// idempotent successes; ProviderExpired/Missing and RequestMissing are terminal for
// this match; RaceLost is "lost the atomic claim" (transient or consent-revocation).
public enum ExchangeOutcome
{
    Exchanged,
    RoutedToChat,
    AlreadyShared,
    AlreadyRouted,
    RaceLost,
    ProviderExpired,
    ProviderMissing,
    RequestMissing,
    InvalidData
}
