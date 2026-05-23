using Hook.Features.Ai.Models;
using Hook.Features.Whatsapp.Models;

namespace Hook.Features.Whatsapp.ReceiveWebhook.ClassifyInboundIntent;

// Carries the inbound to the [NonTransactional] AI step.
public sealed record ClassifyInboundIntentRequested(InboundMessage Message, string Reserved = "");

// Re-enters the transactional router with a prefetched intent, so the
// post-classification switch runs inside a normal Wolverine handler context.
public sealed record RouteClassifiedIntent(
    InboundMessage Message,
    IntentDetectionResult Detected,
    string Reserved = "");
