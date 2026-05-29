using Hook.Features.MetaTemplates;
using Hook.Features.Observability;
using Wolverine.Attributes;

namespace Hook.Features.Tips;

public sealed class RecordTipCooldownHandler(
    IWhatsappContactRepository contacts,
    TimeProvider clock,
    ILogger<RecordTipCooldownHandler> logger)
{
    // [NonTransactional]: this is a single best-effort UPDATE, no aggregate
    // mutation. Failure here MUST NOT fault the envelope — including OCE — or
    // Wolverine would retry the cooldown and there is no idempotency on the
    // jsonb_set side-effect (a second retry is harmless but pointless).
    [NonTransactional]
    public async Task Handle(RecordTipCooldownCommand cmd, CancellationToken ct)
    {
        try
        {
            await contacts.RecordTipAsync(cmd.To.Value, cmd.Trigger, clock.GetUtcNow(), ct);
        }
        catch (Exception ex)
        {
            HookMetrics.TipCooldownPersistFailures.Add(1,
                new KeyValuePair<string, object?>("tip_key", cmd.TipKey));
            logger.LogWarning(ex,
                "Failed to persist tip cooldown for {To} tip={TipKey}",
                cmd.To.Mask(), cmd.TipKey);
        }
    }
}
