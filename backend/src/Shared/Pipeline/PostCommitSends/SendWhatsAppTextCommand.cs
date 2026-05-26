using Hook.Features.Tips;
using Hook.Features.Whatsapp.Phone;

namespace Hook.Shared.Pipeline.PostCommitSends;

// `Tip` opts the dispatch into the contextual-tip throttle:
//   - null  ⇒ never append a tip (default — preserves every existing call site).
//   - value ⇒ ask the TipPicker for a tip eligible for this (phone, trigger); on
//             hit the picked tip is appended to `Text` as a new paragraph and the
//             cooldown is recorded ONLY AFTER the WhatsApp HTTP send succeeds
//             (send-first, record-after — a retry leaks a duplicate tip on the
//             rare second delivery rather than dropping the tip forever).
public sealed record SendWhatsAppTextCommand(
    PhoneNumber To,
    string Text,
    TipTrigger? Tip = null);
