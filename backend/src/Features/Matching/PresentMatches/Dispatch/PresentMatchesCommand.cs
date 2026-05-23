using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Matching.PresentMatches.Dispatch;

public sealed record PresentMatchesCommand(
    PhoneNumber ClientPhone,
    Guid RequestId,
    string ServiceSlug,
    IReadOnlyList<PresentedMatch> Matches,
    string Reserved = "");
