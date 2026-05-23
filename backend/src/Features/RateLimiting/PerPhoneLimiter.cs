using System.Threading.RateLimiting;
using Hook.Features.Observability;
using Microsoft.Extensions.Options;

namespace Hook.Features.RateLimiting;

public sealed class PerPhoneLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _burst;
    private readonly PartitionedRateLimiter<string> _spam;

    public PerPhoneLimiter(IOptions<RateLimitOptions> options)
    {
        var opts = options.Value;

        _burst = PartitionedRateLimiter.Create<string, string>(phone =>
            RateLimitPartition.GetTokenBucketLimiter(phone, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = opts.BurstTokens,
                ReplenishmentPeriod = TimeSpan.FromSeconds(opts.BurstWindowSeconds),
                TokensPerPeriod = opts.BurstTokens,
                AutoReplenishment = true,
                QueueLimit = 0
            }));

        _spam = PartitionedRateLimiter.Create<string, string>(phone =>
            RateLimitPartition.GetFixedWindowLimiter(phone, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = opts.SpamPerHour,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));
    }

    public RateLimitOutcome TryAcquire(string phone)
    {
        using var burstLease = _burst.AttemptAcquire(phone);
        if (!burstLease.IsAcquired)
        {
            HookMetrics.RateLimitBlocks.Add(1, new KeyValuePair<string, object?>("reason", "burst"));
            return RateLimitOutcome.Burst(GetRetryAfter(burstLease));
        }

        using var spamLease = _spam.AttemptAcquire(phone);
        if (!spamLease.IsAcquired)
        {
            HookMetrics.RateLimitBlocks.Add(1, new KeyValuePair<string, object?>("reason", "spam"));
            return RateLimitOutcome.Spam(GetRetryAfter(spamLease));
        }

        return RateLimitOutcome.Allowed();
    }

    private static TimeSpan GetRetryAfter(RateLimitLease lease)
    {
        return lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) && retry is TimeSpan ts
            ? ts
            : TimeSpan.FromSeconds(5);
    }

    public void Dispose()
    {
        _burst.Dispose();
        _spam.Dispose();
    }
}

public sealed record RateLimitOutcome(bool IsAllowed, RateLimitReason Reason, TimeSpan RetryAfter)
{
    public static RateLimitOutcome Allowed() => new(true, RateLimitReason.None, TimeSpan.Zero);
    public static RateLimitOutcome Burst(TimeSpan retry) => new(false, RateLimitReason.Burst, retry);
    public static RateLimitOutcome Spam(TimeSpan retry) => new(false, RateLimitReason.Spam, retry);
}

public enum RateLimitReason
{
    None,
    Burst,
    Spam
}
