using Hook.Features.MetaTemplates;
using Hook.Features.Tips;
using Hook.Features.Whatsapp;
using Microsoft.Extensions.Options;
using Wolverine.Attributes;

namespace Hook.Shared.Pipeline.PostCommitSends;

public sealed class SendWhatsAppTextHandler(
    IWhatsappClient whatsapp,
    ITipPicker tipPicker,
    IWhatsappContactRepository contacts,
    IOptions<TipOptions> tipOptions,
    TimeProvider clock)
{
    // Fire-and-forget WhatsApp HTTP — no EF state to commit. Opting out of
    // AutoApplyTransactions avoids pinning an Npgsql connection per dispatch.
    // Send first, record after: on a rare HTTP-retry the worst case is a
    // duplicate tip on the second delivery, which the user can tolerate —
    // the inverse order (record-then-send) silently drops the tip forever on
    // any retry because the cooldown is already set when PickAsync runs again.
    [NonTransactional]
    public async Task Handle(SendWhatsAppTextCommand cmd, CancellationToken ct)
    {
        var body = cmd.Text;
        Tip? tip = null;

        if (cmd.Tip is { } trigger && tipOptions.Value.Enabled)
        {
            tip = await tipPicker.PickAsync(cmd.To.Value, trigger, ct);
            if (tip is not null) body = $"{body}\n\n{tip.Text}";
        }

        await whatsapp.SendTextAsync(cmd.To, body, ct);

        if (tip is not null)
            await contacts.RecordTipAsync(cmd.To.Value, tip.Key, clock.GetUtcNow(), ct);
    }
}
