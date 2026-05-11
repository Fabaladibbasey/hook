using Hook.Features.Whatsapp;

namespace Hook.Shared.Pipeline.PostCommitSends;

public sealed class SendWhatsAppTextHandler(IWhatsappClient whatsapp)
{
    public Task Handle(SendWhatsAppTextRequested evt, CancellationToken ct) =>
        whatsapp.SendTextAsync(evt.To, evt.Text, ct);
}
