using Hook.Features.Ai.Models;
using Hook.Features.Tips;
using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Whatsapp.ReceiveWebhook.ColdReply;

// `Tip` propagates to the SendWhatsAppTextCommand the handler ultimately publishes,
// so contextual tips can ride on cold-path replies. See SendWhatsAppTextCommand
// for cooldown / persistence semantics.
public sealed record SendColdReplyCommand(
    PhoneNumber To,
    string Text,
    IntentDetectionResult Detected,
    string Purpose,
    TipTrigger? Tip = null);
