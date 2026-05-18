using Hook.Features.Whatsapp;
using Wolverine.Attributes;

namespace Hook.Shared.Pipeline.PostCommitSends;

public sealed class SendWhatsAppTextHandler(IWhatsappClient whatsapp)
{
    // Fire-and-forget WhatsApp HTTP — no EF state to commit. Opting out of
    // AutoApplyTransactions avoids pinning an Npgsql connection per dispatch.
    [NonTransactional]
    public Task Handle(SendWhatsAppTextRequested evt, CancellationToken ct) =>
        whatsapp.SendTextAsync(evt.To, evt.Text, ct);
}
