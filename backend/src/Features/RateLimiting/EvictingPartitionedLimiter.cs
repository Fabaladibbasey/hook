using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace Hook.Features.RateLimiting;

// Singleton-safe partitioned limiter w/ idle eviction. PartitionedRateLimiter.Create
// holds keys forever; per-participant / per-phone keys grow monotonically over the
// process lifetime. This wrapper tracks last-seen per key and a periodic Sweep drops
// keys idle past the configured TTL.
public sealed class EvictingPartitionedLimiter<TKey>(
    Func<RateLimiter> limiterFactory,
    TimeSpan idleEvictAfter,
    TimeProvider clock) : IDisposable
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Entry> _entries = new();

    public RateLimitLease AttemptAcquire(TKey key)
    {
        var now = clock.GetUtcNow();
        var entry = _entries.GetOrAdd(key, _ => new Entry(limiterFactory(), now));
        entry.LastSeenUtc = now;
        return entry.Limiter.AttemptAcquire(1);
    }

    // Drops keys idle past idleEvictAfter. Safe to call concurrently with AttemptAcquire;
    // a key replenished between cutoff snapshot and TryRemove is detected by the
    // LastSeenUtc re-check inside TryRemove via the snapshot pair.
    public int Sweep()
    {
        var cutoff = clock.GetUtcNow() - idleEvictAfter;
        var removed = 0;
        foreach (var (key, entry) in _entries)
        {
            if (entry.LastSeenUtc >= cutoff) continue;
            if (!_entries.TryRemove(new KeyValuePair<TKey, Entry>(key, entry))) continue;
            entry.Limiter.Dispose();
            removed++;
        }
        return removed;
    }

    public int ActiveKeyCount => _entries.Count;

    public void Dispose()
    {
        foreach (var entry in _entries.Values) entry.Limiter.Dispose();
        _entries.Clear();
    }

    private sealed class Entry(RateLimiter limiter, DateTimeOffset lastSeenUtc)
    {
        public RateLimiter Limiter { get; } = limiter;
        public DateTimeOffset LastSeenUtc { get; set; } = lastSeenUtc;
    }
}
