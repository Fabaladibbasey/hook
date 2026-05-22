# Hook — Operations Run Book

Production deploy targets a single Hetzner Cloud VPS (`hook.drop.africa`) running Docker Compose behind Caddy with auto Let's Encrypt. The workflow lives at `.github/workflows/deploy-hetzner.yml` and is named to leave room for future deploy-to-other-VPS workflows.

## Stack

| Service     | Image                              | Role                                   |
|-------------|------------------------------------|----------------------------------------|
| `caddy`     | `caddy:2-alpine`                   | TLS termination + reverse proxy        |
| `api`       | `ghcr.io/<org>/hook:<tag>`         | ASP.NET host (API + SignalR + SPA)     |
| `postgres`  | `postgis/postgis:16-3.4`           | Postgres 16 + PostGIS + pg_trgm        |
| `ollama`    | `ollama/ollama:latest`             | Local LLM inference (qwen2.5:3b default) |
| `seq`       | `datalust/seq:latest`              | Structured log aggregation (internal, via Caddy) |
| `backup`    | `postgis/postgis:16-3.4` + cron    | Daily `pg_dump` to `hook-backups`      |

All services share `hook-net`. Caddy is the only public surface (ports 80/443). `api` depends on both `postgres` and `ollama` being healthy before starting.

## First-time setup

1. **Provision VPS** with Docker Engine + Compose plugin, open ports 80/443.
2. **Point DNS** for `HOOK_DOMAIN` at the VPS public IP (A/AAAA).
3. **Clone deploy bundle:**
   ```sh
   git clone <repo> /opt/hook && cd /opt/hook
   ```
4. **Create `.env.prod`** from `.env.example`:
   ```sh
   cp .env.example .env.prod
   chmod 600 .env.prod
   # edit values
   ```
5. **First boot:**
   ```sh
   docker compose -f docker-compose.prod.yml --env-file .env.prod pull
   docker compose -f docker-compose.prod.yml --env-file .env.prod up -d
   ```
6. **Pull the AI model** (blocks until downloaded; `/readyz` returns 503 until complete):
   ```sh
   docker compose -f docker-compose.prod.yml exec ollama ollama pull ${OLLAMA_MODEL:-qwen2.5:3b}
   ```
7. **Migrations** run on container start (the API applies EF migrations against `postgres` once it's healthy).
8. **Verify:**
   ```sh
   curl -fsS https://${HOOK_DOMAIN}/healthz
   curl -fsS https://${HOOK_DOMAIN}/metrics | head
   ```

## Required environment variables

See `.env.example` for the full list. Categories:

| Group              | Vars                                                                   |
|--------------------|------------------------------------------------------------------------|
| Domain / TLS       | `HOOK_DOMAIN`, `ACME_EMAIL`                                            |
| Image              | `HOOK_IMAGE`                                                           |
| Postgres           | `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD`                    |
| WhatsApp           | `WHATSAPP_VERIFY_TOKEN`, `WHATSAPP_APP_SECRET`, `WHATSAPP_PHONE_NUMBER_ID`, `WHATSAPP_ACCESS_TOKEN`, `WHATSAPP_GRAPH_API_VERSION` |
| Ollama             | `OLLAMA_BASE_URL` (default `http://ollama:11434`), `OLLAMA_MODEL` (default `qwen2.5:3b`) |
| Google geocoding   | `GOOGLE_GEOCODING_API_KEY`                                             |
| Seq / logs         | `SEQ_FIRSTRUN_ADMINPASSWORDHASH`, `SEQ_INGEST_API_KEY` (optional), `SEQ_URL` (default `http://seq:5341`), `LOGS_DOMAIN`, `LOGS_BASIC_AUTH_USER`, `LOGS_BASIC_AUTH_HASH` |
| Backups            | `BACKUP_RETENTION_DAYS` (default 14)                                   |

`.env.prod` is git-ignored (`.env.*` glob in `.gitignore`).

## Health & telemetry

- **Liveness:** `GET /healthz` — JSON `{"status":"ok"}`. Used by Docker `HEALTHCHECK` and the deploy smoke probe.
- **Metrics:** `GET /metrics` — Prometheus exposition. Counters: `hook.matches.total`, `hook.matches.pool_size`, `hook.ai.calls.total`, `hook.ai.latency_ms`, `hook.geocode.cache_hits`, `hook.geocode.api_calls`, `hook.whatsapp.outside_window_sends`, `hook.ratelimit.blocks`, plus standard ASP.NET / HTTP client / runtime metrics.
- **Correlation IDs:** every request carries `X-Correlation-Id` (auto-generated when absent). Threads through Serilog `LogContext` — grep logs by id to trace one conversation end-to-end.

## Logs

- API logs: structured JSON to stdout (Docker journal) and rolling file `/app/logs/hook-YYYYMMDD.log` (mounted volume `hook-logs`).
- Caddy access + cert logs: stdout (Docker journal).
- Postgres logs: stdout (Docker journal).

Tail one service:
```sh
docker compose -f docker-compose.prod.yml logs -f api
```

## Backups

- `backup` container runs daily at 03:00 UTC (cron) and writes `hook-<UTC>.dump.gz` into the `hook-backups` volume.
- Retention: `BACKUP_RETENTION_DAYS` (default 14).
- **Off-host copy:** schedule a host-side cron to `rsync`/`rclone` `hook-backups` volume to remote storage. Example with rclone-to-S3:
  ```sh
  0 4 * * * docker run --rm -v hook_hook-backups:/data:ro rclone/rclone sync /data s3:hook-backups
  ```
- **Restore:**
  ```sh
  docker compose -f docker-compose.prod.yml exec -T postgres pg_restore \
    --clean --if-exists --no-owner --dbname=$POSTGRES_DB \
    < /path/to/hook-<stamp>.dump
  ```
  (gunzip the `.gz` first, or pipe via `zcat`.)

## Rollback

CI tags every image with both `:latest` and `:<short-sha>`. To roll back:

```sh
cd /opt/hook
export HOOK_IMAGE=ghcr.io/<org>/hook:<previous-sha>
docker compose -f docker-compose.prod.yml --env-file .env.prod pull api
docker compose -f docker-compose.prod.yml --env-file .env.prod up -d api
curl -fsS https://${HOOK_DOMAIN}/healthz
```

For schema rollback, restore from the latest pre-deploy `pg_dump` (see Backups above). EF migrations are forward-only by convention — never edit a shipped migration; write a new one.

## CI / CD

- **Build & Test** (`.github/workflows/build-and-test.yml`): build + test on every push/PR. Postgres service container is provisioned for integration tests.
- **Deploy** (`.github/workflows/deploy-hetzner.yml`): on push to `main` or tag `v*`:
  1. Build multi-stage image, push to GHCR with tags `:<sha>` and `:latest`.
  2. SSH into VPS, `docker compose pull api`, `up -d`, prune.
  3. Curl `/healthz` until 200 (10×5s).
- **Required secrets** (GitHub repo → Settings → Secrets → Actions, environment `production`):
  `PROD_SSH_HOST`, `PROD_SSH_USER`, `PROD_SSH_KEY`, `PROD_SSH_PORT` (optional), `PROD_DEPLOY_DIR`, `PROD_DOMAIN`.

## Rate limiting

Two layers protect the API surface:

- **Global limiter** (`Features/RateLimiting/GlobalRateLimitPartitioner`) — fixed-window
  bucket (`RateLimit:GlobalPermitLimit` requests per `RateLimit:GlobalWindowSeconds`)
  applied to every unmatched request. Partition key is either `t:<token>` (length-capped
  at 128 chars) when the request carries `?token=`, otherwise `ip:<RemoteIpAddress>`.
  The length cap bounds dictionary growth so spraying tokens cannot exhaust memory.
- **Webhook concurrency limiter** (`webhook-concurrency` policy) — caps simultaneous
  POSTs to `/webhooks/whatsapp` at `RateLimit:WebhookConcurrencyLimit` permits with
  `RateLimit:WebhookQueueLimit` queued. HMAC validation + 64 KB request-size cap on
  the same endpoint stop large-body amplification.

**Bypass list** (no limiter applied):

| Branch                                  | Why                                                          |
|-----------------------------------------|--------------------------------------------------------------|
| `/webhooks/whatsapp`                    | Already gated by named concurrency policy + HMAC.            |
| `/hubs/chat`                            | Long-lived SignalR transport; per-message limits live in hub.|
| Host listed in `RateLimit:BypassHosts`  | YARP-proxied internal UIs (e.g. Seq).                        |

**Per-phone limiter** (`PerPhoneLimiter`) is registered in DI for future webhook
integration. Today its only consumer is its own unit tests — wiring it into the
inbound flow is tracked separately.

**Forwarded headers trust scope**

Production sits behind Caddy on a private docker bridge. `ForwardedHeaders:KnownNetworks`
defaults to `172.16.0.0/12` (the docker default bridge subnet). `ForwardLimit=1` so
only the last hop's `X-Forwarded-For` entry is honored — a real bridge client cannot
smuggle a forged external IP through multi-hop parsing. The block is gated on
`!IsDevelopment()`; dev runs Kestrel directly with no proxy.

## Troubleshooting

| Symptom                         | Likely cause / first check                                         |
|---------------------------------|--------------------------------------------------------------------|
| `/healthz` 502 from Caddy       | `docker compose ps` — `api` not healthy. Check `docker logs hook-api`. |
| Cert acquisition fails          | DNS not pointing yet, or port 80 not reachable. Check `docker logs hook-caddy`. |
| Webhook 403 every time          | `WHATSAPP_APP_SECRET` mismatch. Verify Meta Business Manager value.|
| AI calls all fail               | Ollama container not healthy or model not pulled. `docker compose ps ollama`; `docker logs hook-ollama`; run `docker compose exec ollama ollama pull <model>` if model missing. Check `hook.ai.calls.total` metric. |
| `outside_window_sends` climbing | Provider check-in timing drifted out of 24h window — investigate scheduling. |
| Backup volume filling up        | Lower `BACKUP_RETENTION_DAYS` or move off-host copy schedule. |
