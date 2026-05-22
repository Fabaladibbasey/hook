using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Feedback.Eta;

public sealed record ExtractEtaRequested(
    Guid PendingId,
    Guid MatchId,
    PhoneNumber From,
    string Text,
    string Reserved = "");

public sealed record ApplyEtaOutcome(
    Guid PendingId,
    Guid MatchId,
    DateTimeOffset? EtaUtc,
    PhoneNumber From,
    string Reserved = "");
