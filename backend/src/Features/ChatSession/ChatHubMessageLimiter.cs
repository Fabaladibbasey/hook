using System.Threading.RateLimiting;
using Hook.Features.Observability;
using Hook.Features.RateLimiting;
using Microsoft.Extensions.Options;

namespace Hook.Features.ChatSession;

// Live-socket hub gate. Method invocations don't traverse the HTTP middleware
// pipeline, so the global rate limiter on negotiate doesn't cover them — this
// token-bucket fills that gap, partitioned by participant id. Backed by
// EvictingPartitionedLimiter so the per-participant map drops idle keys.
public sealed class ChatHubMessageLimiter : IDisposable, ISweepableLimiter
{
    private readonly EvictingPartitionedLimiter<string> _burst;

    public ChatHubMessageLimiter(IOptions<RateLimitOptions> options, TimeProvider clock)
    {
        var opts = options.Value;
        _burst = new EvictingPartitionedLimiter<string>(
            () => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = opts.ChatHubBurstTokens,
                ReplenishmentPeriod = TimeSpan.FromSeconds(opts.ChatHubBurstWindowSeconds),
                TokensPerPeriod = opts.ChatHubBurstTokens,
                AutoReplenishment = true,
                QueueLimit = 0
            }),
            TimeSpan.FromMinutes(opts.LimiterIdleEvictionMinutes),
            clock);
    }

    public RateLimitOutcome TryAcquire(string participantKey)
    {
        using var lease = _burst.AttemptAcquire(participantKey);
        if (lease.IsAcquired) return RateLimitOutcome.Allowed();

        HookMetrics.RateLimitBlocks.Add(1, new KeyValuePair<string, object?>("reason", "chat_burst"));
        var retry = lease.TryGetMetadata(MetadataName.RetryAfter, out var v) && v is TimeSpan ts
            ? ts
            : TimeSpan.FromSeconds(5);
        return RateLimitOutcome.Burst(retry);
    }

    public int SweepIdle() => _burst.Sweep();

    public void Dispose() => _burst.Dispose();
}
