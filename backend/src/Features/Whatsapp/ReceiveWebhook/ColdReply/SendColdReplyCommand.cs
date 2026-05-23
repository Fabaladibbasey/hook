using Hook.Features.Ai.Models;
using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Whatsapp.ReceiveWebhook.ColdReply;

public sealed record SendColdReplyCommand(
    PhoneNumber To,
    string Text,
    IntentDetectionResult Detected,
    string Purpose,
    string Reserved = "");
