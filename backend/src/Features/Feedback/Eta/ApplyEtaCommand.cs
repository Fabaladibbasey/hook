using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Feedback.Eta;

public sealed record ApplyEtaCommand(
    Guid PendingId,
    Guid MatchId,
    DateTimeOffset? EtaUtc,
    PhoneNumber From,
    string Reserved = "");
