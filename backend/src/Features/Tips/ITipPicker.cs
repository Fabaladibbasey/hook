namespace Hook.Features.Tips;

public interface ITipPicker
{
    // Returns the tip to append for this (phone, trigger) pair, or null when:
    // - the feature is disabled (`TipOptions.Enabled = false`),
    // - no tip exists for the trigger,
    // - the cooldown window has not elapsed since the contact's last tip,
    // - the contact row is not present yet (we tip only known contacts).
    Task<Tip?> PickAsync(string phone, TipTrigger trigger, CancellationToken ct = default);
}
