using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Feedback.Step1Intent;

public sealed record ExtractStep1IntentCommand(
    Guid PendingId,
    Guid MatchId,
    PhoneNumber From,
    string Text,
    // Schema-bump slot per CLAUDE.md outbox-compat rule — do not remove.
    string Reserved = "");
