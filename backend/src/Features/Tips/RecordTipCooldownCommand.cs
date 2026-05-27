using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Tips;

// Post-send cooldown persistence for the TipDispatcher user-requested route.
// Separated from SendWhatsAppTextCommand so the dispatcher can ship the picked
// tip body directly (no Tip rider, no handler re-pick, no race window) while
// keeping the cooldown side-effect on its own outbox envelope.
public sealed record RecordTipCooldownCommand(
    PhoneNumber To,
    TipTrigger Trigger,
    string TipKey);
