using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Feedback.Eta;

public sealed record ExtractEtaCommand(
    Guid PendingId,
    Guid MatchId,
    PhoneNumber From,
    string Text,
    string Reserved = "");
