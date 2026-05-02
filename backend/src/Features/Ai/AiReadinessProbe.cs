namespace Hook.Features.Ai;

public sealed record ProbeResult(bool Healthy, DateTimeOffset CheckedAt, string? Error);

public sealed class AiReadinessProbe(
    IConversationAi ai,
    TimeProvider clock,
    ILogger<AiReadinessProbe> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedResult? _cached;

    public async Task<ProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (_cached is { } hit && now - hit.CheckedAt < CacheTtl)
            return new ProbeResult(hit.Healthy, hit.CheckedAt, hit.Error);

        await _gate.WaitAsync(ct);
        try
        {
            now = clock.GetUtcNow();
            if (_cached is { } recheck && now - recheck.CheckedAt < CacheTtl)
                return new ProbeResult(recheck.Healthy, recheck.CheckedAt, recheck.Error);

            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(ProbeTimeout);

            try
            {
                _ = await ai.DetectIntentAsync("ping", probeCts.Token);
                _cached = new CachedResult(true, now, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _cached = new CachedResult(false, now, ex.Message);
                logger.LogWarning(ex, "AI readiness probe failed");
            }

            return new ProbeResult(_cached.Healthy, _cached.CheckedAt, _cached.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record CachedResult(bool Healthy, DateTimeOffset CheckedAt, string? Error);
}
