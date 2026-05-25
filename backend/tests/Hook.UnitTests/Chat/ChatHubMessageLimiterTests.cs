using Hook.Features.ChatSession;
using Hook.Features.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Hook.UnitTests.Chat;

public class ChatHubMessageLimiterTests
{
    [Fact]
    public void TryAcquire_ExhaustingBurstReturnsBurst()
    {
        using var limiter = new ChatHubMessageLimiter(Options.Create(new RateLimitOptions
        {
            ChatHubBurstTokens = 3,
            ChatHubBurstWindowSeconds = 60
        }), new FakeTimeProvider());

        var key = Guid.NewGuid().ToString();
        for (var i = 0; i < 3; i++)
            limiter.TryAcquire(key).IsAllowed.ShouldBeTrue($"call {i + 1} should be allowed");

        var blocked = limiter.TryAcquire(key);
        blocked.IsAllowed.ShouldBeFalse();
        blocked.Reason.ShouldBe(RateLimitReason.Burst);
    }

    [Fact]
    public void TryAcquire_PartitionsByKey()
    {
        using var limiter = new ChatHubMessageLimiter(Options.Create(new RateLimitOptions
        {
            ChatHubBurstTokens = 1,
            ChatHubBurstWindowSeconds = 60
        }), new FakeTimeProvider());

        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();

        limiter.TryAcquire(a).IsAllowed.ShouldBeTrue();
        limiter.TryAcquire(a).IsAllowed.ShouldBeFalse();
        limiter.TryAcquire(b).IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task TryAcquire_Refills_AfterReplenishmentPeriod_AllowsAcquireAgain()
    {
        // Token bucket replenishment is wall-clock driven by the underlying limiter
        // (not the injected TimeProvider — RateLimiting was built before .NET 8 TimeProvider).
        // AutoReplenishment is timer-wheel driven; on busy CI runners the tick can lag the
        // configured period, so poll past the window rather than asserting on a fixed sleep.
        using var limiter = new ChatHubMessageLimiter(Options.Create(new RateLimitOptions
        {
            ChatHubBurstTokens = 1,
            ChatHubBurstWindowSeconds = 1
        }), new FakeTimeProvider());

        var key = Guid.NewGuid().ToString();
        limiter.TryAcquire(key).IsAllowed.ShouldBeTrue();
        limiter.TryAcquire(key).IsAllowed.ShouldBeFalse();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            if (limiter.TryAcquire(key).IsAllowed) return;
        }

        limiter.TryAcquire(key).IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void TryAcquire_Denied_PopulatesRetryAfterMetadata()
    {
        using var limiter = new ChatHubMessageLimiter(Options.Create(new RateLimitOptions
        {
            ChatHubBurstTokens = 1,
            ChatHubBurstWindowSeconds = 60
        }), new FakeTimeProvider());

        var key = Guid.NewGuid().ToString();
        limiter.TryAcquire(key);
        var rejected = limiter.TryAcquire(key);

        rejected.IsAllowed.ShouldBeFalse();
        rejected.RetryAfter.ShouldBeGreaterThan(TimeSpan.Zero);
    }
}
