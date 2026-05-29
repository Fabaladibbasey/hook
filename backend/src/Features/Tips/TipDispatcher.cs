using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;

namespace Hook.Features.Tips;

// User-facing tip route. The bot already appends contextual tips as riders on
// outbound replies (`SendWhatsAppTextCommand.Tip`); this dispatcher gives users
// a way to ASK for one (deterministic "TIP" / "any tip" route in InboundRouter).
//
// Pick ONCE here so a concurrent rider-style dispatch on the same phone cannot
// race the cooldown and leave the user with silence. The picked body ships
// directly in the command Text (no Tip rider, so the handler won't re-pick).
// Cooldown persistence rides a follow-up RecordTipCooldownCommand — keeps the
// send-first / persist-after-success ordering without the empty-body race.
public sealed class TipDispatcher(
    IMessageBus bus,
    ITipPicker picker,
    ILogger<TipDispatcher> logger)
{
    public async ValueTask DispatchAsync(PhoneNumber to, CancellationToken ct)
    {
        var tip = await picker.PickAsync(to.Value, TipTrigger.UserRequested, ct);
        if (tip is null)
        {
            logger.LogDebug("TipDispatcher cooldown-miss for {To}; sending fallback", to.Mask());
            await bus.PublishAsync(new SendWhatsAppTextCommand(to,
                "No new tip right now — try again later."));
            return;
        }

        logger.LogDebug("TipDispatcher pick={TipKey} for {To}", tip.Key, to.Mask());
        await bus.PublishAsync(new SendWhatsAppTextCommand(to, tip.Text));
        await bus.PublishAsync(new RecordTipCooldownCommand(to, TipTrigger.UserRequested, tip.Key));
    }
}
