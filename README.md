# Hook

WhatsApp-funnel + real-time chat platform that connects clients with nearby service providers (delivery, plumbing, carpentry, etc.) without requiring login or an app install.

- **AI conversation layer** — intent detection and clarification (Ollama, mandatory in this build).
- **Distance-aware matching** — PostGIS, ranked by proximity and provider proactiveness.
- **Privacy-controlled handoff** — phone exchange or anonymous chat link.
- **End-to-end encrypted chat** — P-256 ECDH + HKDF-SHA-256 + AES-256-GCM. Server stores ciphertext only.
- **Single-device E2E** — one ECDH-derived AES-GCM key per chat between the two participants; server stores a single ciphertext per message. Opening the chat URL on a new device rotates the participant's `CurrentSessionId`, revoking the prior tab/device on its next hub interaction.

Platform is designed to exit after connecting both parties.

## Stack

| Layer       | Tech                                                                    |
|-------------|-------------------------------------------------------------------------|
| Backend     | .NET 10, ASP.NET Core, Wolverine (in-process messaging), SignalR        |
| Persistence | Postgres 16 + PostGIS 3.4, EF Core 10                                   |
| AI          | Ollama (required — `/readyz` pings AI with 10s cache)                   |
| Frontend    | React 19, TypeScript, Vite                                              |
| Observability | OpenTelemetry, Prometheus, Serilog, Seq                               |
| Container   | `mcr.microsoft.com/dotnet/aspnet:10.0-alpine`, port `8080`              |

## Repo Layout

```
backend/
  src/                    # Hook.csproj (ASP.NET Core, .NET 10)
    Features/             # vertical slices (one folder = one feature)
      Ai, ChatLifecycle, ChatPrivacyRouting, ChatSession,
      ContactSharing, Feedback, Geocoding, Matching,
      MetaTemplates, Observability, ProviderAvailability,
      RateLimiting, ServiceRequest, ServiceTaxonomy, Whatsapp
    Shared/               # cross-cutting infra (auth, persistence, signalr, ...)
  tests/
    Hook.UnitTests/       # xUnit, has InternalsVisibleTo
    Hook.IntegrationTests/
    Hook.TestHelpers/

frontend/
  src/
    features/
      chat/     # ChatRoom, sub-components, useChatHub, chatCrypto
      dev/      # DevConsole
      legal/    # LegalLayout, Terms, Privacy, SupportContact, constants
    components/ # LegalFooter (shared — ChatRoom + LandingPage consume)
    api/        # fetchJson (shared HTTP transport)
    LandingPage.tsx  # root route
    main.tsx

docs/                            # operations, meta-templates, E2E scenarios
prd.md                           # product requirements
```

Each `Features/<Slice>/` owns its handlers, DTOs, persistence calls, and tests (vertical slice).

## Quickstart

### Prereqs

- .NET 10 SDK (`10.0.x`)
- Node 20
- Postgres 16 with PostGIS 3.4
- Ollama running locally (mandatory — no dev stub)

### Database

```
Host=localhost;Port=5432;Database=hook;Username=hook;Password=hook
```

### Backend

```bash
cd backend && dotnet restore
cd backend/src && dotnet run            # binds :5212
```

### Frontend

```bash
cd frontend
npm ci
npm run dev                              # vite at :5173 / :5174
```

For prod, `dotnet publish` automatically runs `npm ci && npm run build` via the `BuildFrontend` MSBuild target and emits the bundle to `backend/src/wwwroot/`. Pass `-p:SkipFrontendBuild=true` to skip.

## Build & Test

From `backend/`:

```bash
dotnet build --configuration Release --no-restore
dotnet test  --configuration Release --no-build
dotnet format
```

From `frontend/`:

```bash
npm run build       # tsc -b && vite build
npm run typecheck   # tsc --noEmit
npm run lint        # eslint src --ext ts,tsx
```

Smoke container publish (mirrors CI):

```bash
cd backend
dotnet publish src/Hook.csproj -c Release /t:PublishContainer -p:ContainerRepository=hook-ci-smoke
```

### Build file-lock workaround

When the dev server holds `Hook.exe`, build to an alt output path:

```bash
cd backend/src
dotnet build -p:BaseOutputPath=bin/altbuild/bin/ -p:BaseIntermediateOutputPath=obj/altbuild/obj/
```

## Conventions

- C#: PascalCase types, `_camelCase` private fields, async methods end in `Async`.
- Test projects: `Hook.<Kind>Tests`. Test classes: `<TypeUnderTest>Tests`. Method names: `Method_State_Expected`.
- Feature folders: PascalCase singular noun (`Matching`, not `Matchings`).
- TypeScript: PascalCase components, camelCase hooks/utils, `*.tsx` for JSX.
- Configuration: `appsettings.<Env>.json`. Never commit `appsettings.Development.json`, `.env*`, `*.pem`, `*.key`, user secrets (already gitignored).

## Behavioral Rules

- **No silent listed-provider inbounds.** Every inbound that extends a provider's TTL must produce a visible WhatsApp reply.
- **Bot-owned action lines.** In matching flow the bot owns the match action line verbatim — `MatchPresenter` does not regenerate it.
- **AI failure = drop.** `AiReplyHelper` drops the message on Ollama failure rather than falling back.
- **Wolverine vs Ollama timeouts.** `Wolverine.DefaultExecutionTimeout` (default 60s) preempts long Ollama calls — keep them aligned in `Program.cs`.
- **Geocoding fixtures.** All coordinates are Banjul / Gambia — dev and integration test fixtures share the same reference point.

## CI / Deploy

- `.github/workflows/ci.yml` — restore, Release build, tests, smoke container publish. `working-directory` = `backend/`. Runner: Ubuntu. Postgres provided by Testcontainers (image `postgis/postgis:16-3.4`); CI runner needs Docker.
- `.github/workflows/deploy.yml` — deploy pipeline.

## More Docs

- `prd.md` — full product requirements
- `docs/operations.md` — ops runbook
- `docs/meta-templates.md` — WhatsApp template management
- `docs/E2E_TEST_SCENARIOS.md` — E2E test scenarios
- `CLAUDE.md` — guidance for AI assistants in this repo
