using Hook.Features.Whatsapp.Models;

namespace Hook.Features.Whatsapp.ReceiveWebhook.ClassifyInboundIntent;

// Carries the inbound to the [NonTransactional] AI step.
public sealed record ClassifyInboundIntentCommand(InboundMessage Message, string Reserved = "");
