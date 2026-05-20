# RateLimiting — runbook

Two HTTP-side limiters protect the API:

- **Global** per-host token bucket: `RateLimit:GlobalPermitLimit` / `GlobalWindowSeconds` / `GlobalQueueLimit`. Rejects abusive bursts before they touch the dispatch pipeline.
- **Webhook concurrency**: `RateLimit:WebhookConcurrencyLimit` / `WebhookQueueLimit` (default 50 / 50). Caps concurrent inbound webhook handlers so the Wolverine + Npgsql pool can't be exhausted by a Meta retry storm.

## Sizing the webhook limiter

Use the ASP.NET rate-limiter OTel counters (auto-emitted via `AddRateLimiter` + `AddOpenTelemetry().WithMetrics()`):

| Metric | What it means |
| --- | --- |
| `aspnetcore.rate_limiting.queued_requests{policy=webhook-concurrency}` | Depth of the wait queue. Sustained >5/min = consider raising `WebhookConcurrencyLimit` (and Npgsql `MaxPoolSize`). |
| `aspnetcore.rate_limiting.rejected_requests{policy=webhook-concurrency}` | Rejected past `QueueLimit`. Non-zero on a healthy host = under-provisioned. |
| `aspnetcore.rate_limiting.request_lease_duration{policy=webhook-concurrency}` | P95 lease hold time. Should track `Wolverine.DefaultExecutionTimeout` minus headroom — if P95 climbs, dispatch is slowing somewhere (Ollama, PG). |

### Tune-up checklist

1. `aspnetcore.rate_limiting.rejected_requests` non-zero for the webhook policy → bump `RateLimit:WebhookConcurrencyLimit` (and confirm Npgsql `MaxPoolSize` ≥ `WebhookConcurrencyLimit + 5` headroom for the outbox).
2. `request_lease_duration` P95 > 5s → root-cause downstream (AI stage backed up, PG slow query, geocoding HTTP timing out). Don't raise the limit until the slowdown is found — raising it just queues more requests on the slow handler.
3. `queued_requests` sustained > 5/min for >10min → traffic genuinely above the configured limit, raise gradually (×1.5) and watch `rejected_requests` go to zero.

### Where the inbound work actually happens

The webhook handler ACKs Meta in <200ms, then queues the message body via Wolverine's durable outbox (T1+commit `0355c3b` deferred AI stages, T1 here deferred geocoding HTTP). The concurrency limit gates the *handler*, not the AI/HTTP work — so a slow Ollama call holds a lease for its full duration. The deferral commits already reduced lease hold from `~10s + AI` down to `<200ms + DB` per inbound; sizing was set with that floor in mind.
