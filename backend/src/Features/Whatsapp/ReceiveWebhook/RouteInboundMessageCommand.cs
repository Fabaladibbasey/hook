using Hook.Features.Whatsapp.Models;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

public sealed record RouteInboundMessageCommand(InboundMessage Message);
