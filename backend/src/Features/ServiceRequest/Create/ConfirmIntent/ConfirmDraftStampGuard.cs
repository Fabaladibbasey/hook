namespace Hook.Features.ServiceRequest.Create.ConfirmIntent;

// Mirrors GeocodeStampGuard — the tick tolerance absorbs Postgres timestamptz
// microsecond truncation (DateTimeOffset is 100ns ticks; the round-trip drops
// to 1µs == 10 ticks). The race we ARE catching (a user reply landing between
// publish and apply) is wall-clock-wide, so the tolerance doesn't collapse the
// guard. envelopeStampedAt == default (no stamp set) short-circuits so a
// publisher that omits the stamp is treated as non-stale.
internal static class ConfirmDraftStampGuard
{
    private const long ToleranceTicks = 10;

    public static bool IsStale(DateTimeOffset draftUpdatedAt, DateTimeOffset envelopeStampedAt)
    {
        if (envelopeStampedAt == default) return false;
        return Math.Abs((draftUpdatedAt - envelopeStampedAt).Ticks) > ToleranceTicks;
    }
}
