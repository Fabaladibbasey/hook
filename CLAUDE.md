# CLAUDE.md

Guidance for Claude Code working in this repo.

## Overview

Hook is a WhatsApp-funnel + real-time chat platform. .NET 10 ASP.NET Core backend with vertical-slice (`Features/`) layout, Postgres + PostGIS persistence, Wolverine for in-process messaging, SignalR for chat. React 19 + TypeScript + Vite frontend (built into `backend/src/wwwroot/` for prod via MSBuild target). True end-to-end encryption: P-256 ECDH + HKDF-SHA-256 + AES-256-GCM; server stores ciphertext only.

## Build & Test Commands

All backend commands run from `backend/` (the dotnet `working-directory` in CI).

```bash
# Restore + build (Release mirrors CI)
cd backend && dotnet restore
cd backend && dotnet build --configuration Release --no-restore

# Run tests (xUnit; produces trx)
cd backend && dotnet test --configuration Release --no-build

# Format
cd backend && dotnet format

# Run dev backend (binds :5212)
cd backend/src && dotnet run

# Smoke container publish (mirrors CI)
cd backend && dotnet publish src/Hook.csproj -c Release /t:PublishContainer -p:ContainerRepository=hook-ci-smoke
```

Frontend (from `frontend/`):

```bash
npm ci
npm run dev          # Vite at :5173 / :5174
npm run build        # tsc -b && vite build
npm run typecheck    # tsc --noEmit
npm run lint         # eslint src --ext ts,tsx
```

Note: `dotnet publish` invokes `npm ci && npm run build` automatically via the `BuildFrontend` MSBuild target in `backend/src/Hook.csproj`. Pass `-p:SkipFrontendBuild=true` to skip.

### Build file-lock workaround

When the dev server holds `Hook.exe`, build to an alt output path so the lock does not block:

```bash
cd backend/src && dotnet build -p:BaseOutputPath=bin/altbuild/bin/ -p:BaseIntermediateOutputPath=obj/altbuild/obj/
```

## Architecture

```
backend/
  src/                    # Hook.csproj (ASP.NET Core, .NET 10)
    Features/             # vertical slices (Ai, Chat*, Matching, Whatsapp, ...)
    Shared/               # cross-cutting infra (auth, persistence, signalr, ...)
    Properties/launchSettings.json
    appsettings*.json
  tests/
    Hook.UnitTests/       # xUnit, has InternalsVisibleTo
    Hook.IntegrationTests/
    Hook.TestHelpers/

frontend/
  src/
    api/        signalr/       crypto/        # transport + E2E primitives
    components/ routes/        App.tsx main.tsx
  vite.config.ts tsconfig.json package.json
```

Key feature slices live under `backend/src/Features/`:
`Ai`, `ChatLifecycle`, `ChatPrivacyRouting`, `ChatSession`, `ContactSharing`, `Feedback`, `Geocoding`, `Matching`, `MetaTemplates`, `Observability`, `ProviderAvailability`, `RateLimiting`, `ServiceRequest`, `ServiceTaxonomy`, `Whatsapp`.

Wolverine messaging runs in-process. `Wolverine.DefaultExecutionTimeout` (default 60s) preempts long Ollama calls — keep them aligned in `Program.cs`.

## Key Patterns

- **Vertical slices**: each `Features/<Slice>/` owns its handlers, DTOs, persistence calls, and tests.
- **AI provider**: Ollama is mandatory — the dev stub has been removed. `AiReplyHelper` drops the message on failure rather than falling back. `/readyz` pings AI with a 10s cache.
- **No silent listed-provider inbounds**: every inbound that extends a provider's TTL must produce a visible WhatsApp reply.
- **Bot-owned action lines**: in matching flow the bot owns the match action line verbatim — do not let `MatchPresenter` regenerate it.
- **Multi-device E2E chat**: SignalR chat ships per-recipient envelopes; server only sees ciphertext.
- **Cross-flow routing**: providers may register against multiple services; routing is flexible.
- **Geocoding**: dev coordinates are Banjul; integration test fixtures are San Francisco.
- **Retention sweep**: `Shared/Retention/` runs a `RetentionHostedService` daily. Configured via `Retention:*` in `appsettings.json`. Disabled by default in tests (`Retention__Enabled=false` env var set in `DevPipelineFixture`); tests that exercise the sweeper construct it directly with explicit options.

## Frontend routes

- `/` — landing page (post-WhatsApp link target).
- `/c/:chatId/:token` — ephemeral E2E chat room.
- `/dev` — dev console (WhatsApp simulation).
- `/terms`, `/privacy` — legal pages, lazy-loaded; `RETENTION_DAYS` literal lives in `frontend/src/legal/RetentionDays.ts` and `SUPPORT_EMAIL` is driven by `VITE_SUPPORT_EMAIL`.

## Naming Conventions

- C#: PascalCase types, `_camelCase` private fields, async methods end in `Async`.
- Test projects: `Hook.<Kind>Tests` (e.g., `Hook.UnitTests`, `Hook.IntegrationTests`).
- Test classes: `<TypeUnderTest>Tests`, methods `Method_State_Expected`.
- Feature folders: PascalCase singular noun (`Matching`, not `Matchings`).
- TypeScript: PascalCase components, camelCase hooks/utils, `*.tsx` for JSX, `*.ts` otherwise.
- Configuration files: `appsettings.<Env>.json`, never commit `appsettings.Development.json` (already gitignored).

## Environment & CI

- **Platform**: Windows 11 dev box. Shell is bash (Git Bash); PowerShell 7+ also available. Use forward slashes and `/dev/null` (not `NUL`). CI runs Ubuntu.
- **Runtime versions**: .NET 10 SDK (`10.0.x`), Node 20, Postgres 16 + PostGIS 3.4 (CI uses `postgis/postgis:16-3.4`).
- **Dev runtime**: backend at `:5212`, vite at `:5173`/`:5174`, Postgres at `:5432`.
- **CI config**: `.github/workflows/ci.yml` — restores, builds Release, runs tests, smoke-publishes a container. `working-directory` defaults to `backend/`.
- **Deploy**: `.github/workflows/deploy.yml`.
- **Secrets**: never commit `.env*`, `appsettings.Development.json`, `*.pem`, `*.key`, user secrets. `.gitignore` already covers these.
- **Connection string** (CI + local default): `Host=localhost;Port=5432;Database=hook;Username=hook;Password=hook`.
