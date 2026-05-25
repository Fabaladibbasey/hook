using Hook.Features.ChatSession;
using Hook.Features.RateLimiting;
using Microsoft.Extensions.Options;
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
        }));

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
        }));

        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();

        limiter.TryAcquire(a).IsAllowed.ShouldBeTrue();
        limiter.TryAcquire(a).IsAllowed.ShouldBeFalse();
        limiter.TryAcquire(b).IsAllowed.ShouldBeTrue();
    }
}
