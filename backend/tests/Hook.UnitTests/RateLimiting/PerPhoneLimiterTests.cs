using Hook.Features.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Hook.UnitTests.RateLimiting;

public class PerPhoneLimiterTests
{
    [Fact]
    public void TryAcquire_ShouldRejectFourthMessageInBurstWindow()
    {
        var limiter = new PerPhoneLimiter(Options.Create(new RateLimitOptions
        {
            BurstTokens = 3,
            BurstWindowSeconds = 5,
            SpamPerHour = 1000
        }), new FakeTimeProvider());

        var phone = "+22070000123";

        for (var i = 0; i < 3; i++)
        {
            limiter.TryAcquire(phone).IsAllowed.ShouldBeTrue($"call {i + 1} should be allowed");
        }

        var rejected = limiter.TryAcquire(phone);
        rejected.IsAllowed.ShouldBeFalse();
        rejected.Reason.ShouldBe(RateLimitReason.Burst);
    }

    [Fact]
    public void TryAcquire_ShouldNotMixBucketsBetweenPhones()
    {
        var limiter = new PerPhoneLimiter(Options.Create(new RateLimitOptions
        {
            BurstTokens = 1,
            BurstWindowSeconds = 60,
            SpamPerHour = 100
        }), new FakeTimeProvider());

        limiter.TryAcquire("+22070000001").IsAllowed.ShouldBeTrue();
        limiter.TryAcquire("+22070000001").IsAllowed.ShouldBeFalse();
        limiter.TryAcquire("+22070000002").IsAllowed.ShouldBeTrue();
    }
}
