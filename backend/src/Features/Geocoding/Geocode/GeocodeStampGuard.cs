namespace Hook.Features.Geocoding.Geocode;

public static class GeocodeStampGuard
{
    // A CANCEL+restart sequence can land the user back at AwaitingLocation on a
    // NEW draft while the original Google round-trip is still in flight; without
    // this the stale coordinates would apply to the new draft. Envelopes carry
    // draft.UpdatedAt at publish time; apply handlers discard envelopes whose
    // stamp no longer matches. The tick tolerance absorbs Postgres timestamptz
    // microsecond truncation; the CANCEL+restart race is many seconds wide so
    // the tolerance does not collapse the guard.
    private const long ToleranceTicks = 10;

    public static bool IsStale(DateTimeOffset draftUpdatedAt, DateTimeOffset envelopeStampedAt)
    {
        if (envelopeStampedAt == default) return false;
        return Math.Abs((draftUpdatedAt - envelopeStampedAt).Ticks) > ToleranceTicks;
    }
}
