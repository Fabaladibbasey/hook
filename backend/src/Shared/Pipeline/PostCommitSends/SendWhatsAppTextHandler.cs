using Hook.Features.MetaTemplates;
using Hook.Features.Observability;
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
    TimeProvider clock,
    ILogger<SendWhatsAppTextHandler> logger)
{
    // Fire-and-forget WhatsApp HTTP — no EF state to commit. Opting out of
    // AutoApplyTransactions avoids pinning an Npgsql connection per dispatch.
    //
    // Send first, record after: an HTTP failure aborts the handler without
    // leaving the cooldown set (which would silently drop the tip forever on
    // retry — the inverse persist-first order had that bug).
    //
    // The post-send RecordTipAsync is best-effort and MUST NOT fault the
    // handler — including OperationCanceledException. A Wolverine retry of
    // SendWhatsAppTextCommand would re-send the WhatsApp text to the user;
    // cancellation here cannot be more important than the duplicate-send
    // rule. Worst case is a tip appearing earlier than the cooldown intended;
    // never a duplicate user-facing send.
    [NonTransactional]
    public async Task Handle(SendWhatsAppTextCommand cmd, CancellationToken ct)
    {
        var body = cmd.Text;
        Tip? tip = null;

        if (cmd.Tip is { } trigger && tipOptions.Value.Enabled)
        {
            tip = await tipPicker.PickAsync(cmd.To.Value, trigger, ct);
            if (tip is not null)
                body = body.Length == 0 ? tip.Text : $"{body}\n\n{tip.Text}";
        }

        // Defensive: TipDispatcher used to publish empty-body + Tip rider; if
        // the picker raced (cooldown set between dispatcher peek and handler
        // pick), `body` stays empty AND a Tip rider was requested — skip the
        // send rather than ship empty. Empty body WITHOUT a Tip rider is an
        // upstream bug; pass it through so the WhatsApp HTTP layer (or its
        // logs) surface it instead of silently dropping.
        if (body.Length == 0 && cmd.Tip is not null) return;

        await whatsapp.SendTextAsync(cmd.To, body, ct);

        if (tip is null) return;

        try
        {
            // cmd.Tip is non-null here: we only entered the tip branch because cmd.Tip
            // had a value, and `tip is null` short-circuits above.
            await contacts.RecordTipAsync(cmd.To.Value, cmd.Tip!.Value, clock.GetUtcNow(), ct);
        }
        catch (Exception ex)
        {
            HookMetrics.TipCooldownPersistFailures.Add(1,
                new KeyValuePair<string, object?>("tip_key", tip.Key));
            logger.LogWarning(ex,
                "Failed to persist tip cooldown for {To} tip={TipKey} — cooldown not enforced this round",
                cmd.To.Mask(), tip.Key);
        }
    }
}
