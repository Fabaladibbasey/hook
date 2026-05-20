using Hook.Features.Matching.MatchAggregate;
using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Matching.PresentMatches.Dispatch;

public sealed record MatchPresentationDto(
    string ProviderPhone,
    double DistanceKm,
    double Score,
    MatchKind Kind);

public sealed record PresentMatchesRequested(
    PhoneNumber ClientPhone,
    Guid RequestId,
    string ServiceSlug,
    IReadOnlyList<MatchPresentationDto> Matches,
    string Reserved = "");
