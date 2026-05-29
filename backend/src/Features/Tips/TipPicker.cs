using System.IO.Hashing;
using System.Text;
using Hook.Features.MetaTemplates;
using Microsoft.Extensions.Options;

namespace Hook.Features.Tips;

public sealed class TipPicker(
    IWhatsappContactRepository contacts,
    IOptions<TipOptions> options,
    TimeProvider clock) : ITipPicker
{
    public async Task<Tip?> PickAsync(string phone, TipTrigger trigger, CancellationToken ct = default)
    {
        var opts = options.Value;
        if (!opts.Enabled) return null;
        if (!TipCatalog.ByTrigger.TryGetValue(trigger, out var candidates) || candidates.Count == 0)
            return null;

        var state = await contacts.GetForTipsAsync(phone, ct);
        if (state is null) return null;

        // Deterministic stable pick — same phone always maps to the same tip for a
        // trigger. Cooldown is per-trigger: an AfterWelcome cooldown does not block
        // a UserRequested tip on the same contact. Missing key = no prior send,
        // gates trivially open. Per-tip MinIntervalHours raises the floor only.
        //
        // Multiple tips per trigger bucket provide catalog variety across the user
        // population (different phones hash to different indices) but do NOT rotate
        // for a single returning user — the pick is fixed by phone hash. If per-user
        // rotation is ever desired, mix lastSent.Ticks/cooldownTicks into HashIndex.
        var index = HashIndex(phone, candidates.Count);
        var tip = candidates[index];

        var cooldownHours = tip.MinIntervalHours > 0 ? tip.MinIntervalHours : opts.DefaultCooldownHours;
        var lastSent = state.Cooldowns.GetValueOrDefault(trigger, DateTimeOffset.MinValue);
        var now = clock.GetUtcNow();
        if (now - lastSent < TimeSpan.FromHours(cooldownHours))
            return null;

        return tip;
    }

    private static int HashIndex(string phone, int modulus)
    {
        // XxHash32: non-cryptographic, spec-defined → cross-architecture stable.
        // Replaces SHA-1 + BitConverter (which was host-endian) so a phone hashes
        // identically on every runtime.
        var hash = XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(phone));
        return (int)(hash % (uint)modulus);
    }
}
