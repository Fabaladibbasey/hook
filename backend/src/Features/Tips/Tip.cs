namespace Hook.Features.Tips;

// `Key` is recorded in WhatsappContact.LastTipKey for observability. Cooldown is
// a time-only check on LastTipAt — renaming a key does NOT reset the throttle.
// Keep keys short (<=64 chars to fit the column) and globally unique across triggers.
// `MinIntervalHours = 0` falls back to `TipOptions.DefaultCooldownHours`.
public sealed record Tip(string Key, TipTrigger Trigger, string Text, int MinIntervalHours = 0);
