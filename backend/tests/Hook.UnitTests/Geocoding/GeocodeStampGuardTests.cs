using Hook.Features.Geocoding.Geocode;
using Shouldly;

namespace Hook.UnitTests.Geocoding;

public class GeocodeStampGuardTests
{
    private static readonly DateTimeOffset Base =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsStale_DefaultEnvelopeStamp_AlwaysFresh()
    {
        // Pre-fix envelopes serialized without DraftStampedAt deserialize to default;
        // back-compat requires those to be treated as fresh (guard skipped).
        GeocodeStampGuard.IsStale(Base, default).ShouldBeFalse();
    }

    [Fact]
    public void IsStale_ExactMatch_Fresh()
    {
        GeocodeStampGuard.IsStale(Base, Base).ShouldBeFalse();
    }

    [Fact]
    public void IsStale_OneTickDrift_Fresh()
    {
        GeocodeStampGuard.IsStale(Base.AddTicks(1), Base).ShouldBeFalse();
    }

    [Fact]
    public void IsStale_AtToleranceCeiling_Fresh()
    {
        GeocodeStampGuard.IsStale(Base.AddTicks(10), Base).ShouldBeFalse();
        GeocodeStampGuard.IsStale(Base.AddTicks(-10), Base).ShouldBeFalse();
    }

    [Fact]
    public void IsStale_JustPastTolerance_Stale()
    {
        GeocodeStampGuard.IsStale(Base.AddTicks(11), Base).ShouldBeTrue();
        GeocodeStampGuard.IsStale(Base.AddTicks(-11), Base).ShouldBeTrue();
    }

    [Fact]
    public void IsStale_FarFutureDraftStamp_Stale()
    {
        GeocodeStampGuard.IsStale(Base.AddYears(1), Base).ShouldBeTrue();
    }

    [Fact]
    public void IsStale_FarFutureEnvelopeStamp_Stale()
    {
        // Symmetric in both directions — only delta magnitude matters.
        GeocodeStampGuard.IsStale(Base, Base.AddYears(1)).ShouldBeTrue();
    }
}
