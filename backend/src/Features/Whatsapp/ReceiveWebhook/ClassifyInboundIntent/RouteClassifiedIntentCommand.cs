using Hook.Features.Ai.Models;
using Hook.Features.Whatsapp.Models;

namespace Hook.Features.Whatsapp.ReceiveWebhook.ClassifyInboundIntent;

// Re-enters the transactional router with a prefetched intent, so the
// post-classification switch runs inside a normal Wolverine handler context.
public sealed record RouteClassifiedIntentCommand(
    InboundMessage Message,
    IntentDetectionResult Detected,
    string Reserved = "");
