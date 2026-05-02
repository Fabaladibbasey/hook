using Hook.Features.Geocoding.Models;
using Shouldly;

namespace Hook.UnitTests.ProviderAvailability;

public class ProviderAvailabilityTests
{
    [Fact]
    public void Register_ShouldSetExpiryToNowPlusTtl()
    {
        var now = DateTimeOffset.Parse("2026-05-01T12:00:00Z");
        var ttl = TimeSpan.FromHours(24);

        var availability = Hook.Features.ProviderAvailability.AvailabilityAggregate.ProviderAvailability.Register(
            "+12025550123",
            new[] { "plumbing" },
            new Location(13.4549, -16.5790),
            "Banjul",
            shareContact: true,
            ttl,
            now);

        availability.LastActiveAt.ShouldBe(now);
        availability.ExpiresAt.ShouldBe(now + ttl);
        availability.ShareContact.ShouldBeTrue();
        availability.Services.ShouldContain("plumbing");
        availability.Location.SRID.ShouldBe(4326);
        availability.IsActive(now.AddMinutes(1)).ShouldBeTrue();
        availability.IsActive(now.AddHours(25)).ShouldBeFalse();
    }

    [Fact]
    public void Heartbeat_ShouldExtendExpiry()
    {
        var start = DateTimeOffset.Parse("2026-05-01T12:00:00Z");
        var ttl = TimeSpan.FromHours(24);
        var availability = Hook.Features.ProviderAvailability.AvailabilityAggregate.ProviderAvailability.Register(
            "+12025550123", new[] { "plumbing" }, new Location(0, 0), "x", true, ttl, start);

        var later = start.AddHours(20);
        availability.Heartbeat(ttl, later);

        availability.LastActiveAt.ShouldBe(later);
        availability.ExpiresAt.ShouldBe(later + ttl);
    }
}
