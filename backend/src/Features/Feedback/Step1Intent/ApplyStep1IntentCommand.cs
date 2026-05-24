using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Feedback.Step1Intent;

public sealed record ApplyStep1IntentCommand(
    Guid PendingId,
    Guid MatchId,
    Step1ReplyIntent Intent,
    DateTimeOffset? Eta,
    PhoneNumber From,
    // Schema-bump slot per CLAUDE.md outbox-compat rule — do not remove.
    string Reserved = "");
