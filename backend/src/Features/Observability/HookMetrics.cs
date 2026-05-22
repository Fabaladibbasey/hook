using System.Diagnostics.Metrics;

namespace Hook.Features.Observability;

public static class HookMetrics
{
    public const string MeterName = "Hook";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> MatchesTotal =
        Meter.CreateCounter<long>("hook.matches.total");

    public static readonly Histogram<int> MatchesPoolSize =
        Meter.CreateHistogram<int>("hook.matches.pool_size");

    public static readonly Counter<long> AiCallsTotal =
        Meter.CreateCounter<long>("hook.ai.calls.total");

    public static readonly Histogram<double> AiLatencyMs =
        Meter.CreateHistogram<double>("hook.ai.latency_ms");

    public static readonly Counter<long> AiOutboundDropped =
        Meter.CreateCounter<long>("hook.ai.outbound_dropped");

    public static readonly Counter<long> AiClassifyFailures =
        Meter.CreateCounter<long>("hook.ai.classify_failures");

    public static readonly Counter<long> ChatHubFaults =
        Meter.CreateCounter<long>("hook.chat_hub.faults");

    public static readonly Counter<long> GeocodeCacheHits =
        Meter.CreateCounter<long>("hook.geocode.cache_hits");

    public static readonly Counter<long> GeocodeApiCalls =
        Meter.CreateCounter<long>("hook.geocode.api_calls");

    public static readonly Counter<long> WhatsappOutsideWindowSends =
        Meter.CreateCounter<long>("hook.whatsapp.outside_window_sends");

    public static readonly Counter<long> RateLimitBlocks =
        Meter.CreateCounter<long>("hook.ratelimit.blocks");

    public static readonly Counter<long> RetentionDeleted =
        Meter.CreateCounter<long>("hook.retention.deleted.total");

    public static readonly Histogram<double> RetentionSweepDuration =
        Meter.CreateHistogram<double>("hook.retention.sweep.duration_ms");

    public static readonly Counter<long> RetentionSweepErrors =
        Meter.CreateCounter<long>("hook.retention.sweep.errors");

    public static readonly Counter<long> DlqIndexBootstrapMissedRetries =
        Meter.CreateCounter<long>("hook.dlq_index_bootstrap.missed_retries");
}
