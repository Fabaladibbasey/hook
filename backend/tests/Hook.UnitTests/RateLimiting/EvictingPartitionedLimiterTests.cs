using System.Threading.RateLimiting;
using Hook.Features.RateLimiting;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Hook.UnitTests.RateLimiting;

public class EvictingPartitionedLimiterTests
{
    private static EvictingPartitionedLimiter<string> Build(
        FakeTimeProvider clock,
        TimeSpan idleTtl,
        int tokenLimit = 2)
    {
        return new EvictingPartitionedLimiter<string>(
            () => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = tokenLimit,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                TokensPerPeriod = tokenLimit,
                AutoReplenishment = false,
                QueueLimit = 0
            }),
            idleTtl,
            clock);
    }

    [Fact]
    public void AttemptAcquire_NewKey_AllocatesAndAllows()
    {
        var clock = new FakeTimeProvider();
        using var limiter = Build(clock, TimeSpan.FromMinutes(30));

        using var lease = limiter.AttemptAcquire("k1");
        lease.IsAcquired.ShouldBeTrue();
        limiter.ActiveKeyCount.ShouldBe(1);
    }

    [Fact]
    public void AttemptAcquire_SameKey_SharesBucket()
    {
        var clock = new FakeTimeProvider();
        using var limiter = Build(clock, TimeSpan.FromMinutes(30), tokenLimit: 2);

        limiter.AttemptAcquire("k").IsAcquired.ShouldBeTrue();
        limiter.AttemptAcquire("k").IsAcquired.ShouldBeTrue();
        limiter.AttemptAcquire("k").IsAcquired.ShouldBeFalse();
        limiter.ActiveKeyCount.ShouldBe(1);
    }

    [Fact]
    public void Sweep_RemovesKeysIdlePastTtl_DoesNotRemoveActive()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var limiter = Build(clock, TimeSpan.FromMinutes(10));

        limiter.AttemptAcquire("stale").Dispose();
        clock.Advance(TimeSpan.FromMinutes(5));
        limiter.AttemptAcquire("active").Dispose();
        clock.Advance(TimeSpan.FromMinutes(8)); // stale now idle 13m, active idle 8m

        var removed = limiter.Sweep();

        removed.ShouldBe(1);
        limiter.ActiveKeyCount.ShouldBe(1);
        // active still allowed; stale would re-allocate a fresh bucket if touched again.
        limiter.AttemptAcquire("active").IsAcquired.ShouldBeTrue();
    }

    [Fact]
    public void Sweep_AcquireAfterEviction_StartsFreshBucket()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var limiter = Build(clock, TimeSpan.FromMinutes(10), tokenLimit: 1);

        limiter.AttemptAcquire("k").IsAcquired.ShouldBeTrue();
        limiter.AttemptAcquire("k").IsAcquired.ShouldBeFalse(); // bucket exhausted
        clock.Advance(TimeSpan.FromMinutes(20));
        limiter.Sweep().ShouldBe(1);

        // Re-touch after eviction -> fresh bucket, allowed again.
        limiter.AttemptAcquire("k").IsAcquired.ShouldBeTrue();
    }
}
