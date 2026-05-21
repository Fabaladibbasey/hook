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

# Apply pending EF migrations (host no longer auto-migrates at boot — fails fast if pending)
cd backend && dotnet tool restore && dotnet ef database update --project src/Hook.csproj

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
- **Single-device E2E chat**: SignalR uses one ECDH-derived AES-GCM key per chat between the two participants; server stores a single ciphertext per message. Opening the chat URL on a new device rotates the participant's `CurrentSessionId` (revoking the prior tab on its next hub interaction) AND clears the participant's stored `PublicKey` and `LastInboundSequence` — the new device must republish a fresh public key, after which the peer re-derives the shared key. Pre-rotation history persists as ciphertext but renders as undecryptable placeholders on devices without the original keypair in `localStorage`. AAD binds chatId + sender + recipient + messageId + sequence to prevent reflection and junk-injection. Duplicate `messageId` is rejected with `MessageRejectReason.Duplicate` (PK conflict on `chat_messages.id`; no row inserted, no sequence advance).
- **Cross-flow routing**: providers may register against multiple services; routing is flexible.
- **Geocoding**: all coordinates are Banjul / Gambia — dev defaults, integration test fixtures, and unit-test stubs share the same reference point.
- **Retention sweep**: `Shared/Retention/` runs a `RetentionHostedService` daily. Configured via `Retention:*` in `appsettings.json`. Disabled by default in tests via `b.UseSetting("Retention:Enabled","false")` in `DevPipelineFixture`; tests that exercise the sweeper construct it directly with explicit options.
- **`ExchangeOutcome` contract**: nine values split by terminal vs transient failure mode. `Exchanged`/`RoutedToChat` are fresh successes; `AlreadyShared`/`AlreadyRouted` are idempotent re-picks (per-match notice already sent); `RaceLost` is "lost the atomic claim" (covers consent revocation between read and claim); `ProviderExpired`/`ProviderMissing`/`RequestMissing` are terminal for that match; `InvalidData` is a phone-parse failure. Switch consumers must include a `default` arm.
- **Partial unique index + 23505 catch idiom**: `DbSetExtensions.TryInsertUniqueAsync(db, entity, constraintNames, ct)` wraps the insert + Postgres `23505` catch by constraint name, returning `false` on the unique race. The insert runs inside a savepoint when an outer Wolverine handler tx is present, so a lost race does not poison the enclosing transaction. Constraint names live in per-feature constants (`FeedbackConstants.PendingUniqueIndexName`, `FeedbackConstants.RequestStep1UniqueIndexName`, `MatchConstants.RequestProviderUniqueIndexName`) so the entity config, the migration, and the catch site all reference one source of truth.
- **`TimeProvider` injection**: services that need current time (`MatchingService`, `FeedbackResponseService`, `PhoneExchanger`) accept `TimeProvider` via DI. Tests inject a fixed-time provider; production uses `TimeProvider.System`.
- **`WhatsappContact.UpsertInboundAsync`**: every routed inbound writes the contact's `LastInboundAt` AFTER the cancel/abandon detection — a CANCEL message that tears down a draft must not extend the contact's last-inbound timestamp.
- **Feedback Pillar A / Pillar B**: Step1 ("did you find a provider?") fires at `Feedback:Step1InitialDelay` after a successful contact-share or chat-route. **Pillar A**: on `Step1=Yes` the handler publishes `Step2FeedbackCheck` *immediately* (single-pick) or first runs an `IdentifyWinner` step (multi-pick) — there is no separate +20h Step2 delay knob. **Pillar B**: on `Step2=InProgress` the handler reserves an `AwaitingEta` Pending row and asks the client when the job will be done; the parsed ETA drives the next recheck at `eta + EtaScheduleBuffer`. Unparseable ETA past `ParseRetryWindow` falls back to `Step2InProgressRecheckDelay`. ETA is persisted on the `AwaitingEta` row (`EtaUtc`) for audit; ETAs beyond `MaxEtaHorizon` (default 7d) are treated as parse hallucinations and fall back to the fixed delay.
- **Bot-owned multi-pick list**: the `IdentifyWinner` "Which provider worked out? — 1) +220...XX, 2) ..." line is owned by the bot deterministically through `PickedMatchListFormatter`, in `MatchRepository.GetForRequestAsync` production order (`Score DESC, DistanceKm, CreatedAt, Id`) — same enumeration the original `MatchPresenter` `PICK 1/2/3` used, so the client's positional reply binds back to the correct match. Non-Exact rows (Broadened, Narrowed) carry a trailing ` ({MatchLabels.Related})` tag so the client sees the same hierarchy hint that `MatchPresenter` surfaces in the initial match list.
- **Service taxonomy hierarchy**: `Service` is a tree (parent / children via `Service.ParentSlug` self-FK, `OnDelete(SetNull)`). 16 root sectors are seeded at boot by `RootSectorSeeder.EnsureRootSectorsAsync` (idempotent via `TryInsertUniqueAsync` against `PK_services`). New non-root slugs created by `SlugResolver` publish `JudgeParentSlugRequested` → `JudgeParentSlugDispatchHandler` runs Ollama → `bus.InvokeAsync(AssignServiceParent)` → `AssignServiceParentHandler` mutates the aggregate. Matching uses `IServiceRepository.ExpandAsync(slug)` to produce `ExpandedSlugs(Requested, Parent?, Children)`; `MatchKind` records whether a candidate matched the requested slug (`Exact`), the parent the query broadened to (`Broadened`, harsher score discount via `BroadenedMatchFactor`), or a child the query narrowed to (`Narrowed`, modest discount via `NarrowedMatchFactor`).
- **Wolverine durable persistence (always-on)**: Wolverine persists scheduled messages + outbox in the `wolverine` Postgres schema in every environment, including tests. `opts.Policies.UseDurableLocalQueues()` promotes in-process local queues to durable so envelopes published to `[NonTransactional]` handlers (the AI stages) survive a crash between publish and inner-commit. The schema is created by Wolverine itself, **not** by EF migrations — separate lifecycle from the `public` schema. Per-shard isolation relies on each test fixture using its own Postgres database; the `wolverine.*` schema lives inside the same DB and gets torn down with it. The `AutoApplyTransactions` policy + `UseEntityFrameworkCoreTransactions` make every handler run inside an EF transaction with outgoing envelopes written in the same commit. Transport-level dedupe comes from the `wolverine_incoming_envelopes` PK; handler-level idempotency is each slice's responsibility (e.g. `match.ChatId is not null` checks, partial unique indexes on `MatchFeedback`). Schema additions to records persisted in the outbox MUST be backward-compatible — give new positional fields a default value (e.g. `string Reserved = ""`) so envelopes serialised before the deploy continue to deserialise. Dead-letter rows (handler failure past `MaxAttempts`) hold the original JSON body, which may include user text + unmasked phone for AI-stage envelopes — `RetentionSweeper` prunes `wolverine.wolverine_dead_letter_queue` rows past `Retention:DeadLetterRetentionDays` (default 7d) so PII does not accumulate at rest.
- **Post-commit external sends (feedback + chat-routing + matching + funnel orchestration)**: external side effects (WhatsApp HTTP, SignalR broadcasts, AI-generate-then-send pairings) **must** run via the outbox, not inline. Publish `SendWhatsAppTextRequested` / `BroadcastChatEventRequested` / `Step1PromptDispatchRequested` / `Step2PromptDispatchRequested` / `PresentMatchesRequested` / `ExtractServicesRequested` / `RegistrationExtractServicesRequested` / `ExtractEtaRequested` / `ApplyEtaOutcome` / `ClassifyInboundIntentRequested` / `RouteClassifiedIntent` / `SendColdReplyRequested` from a Wolverine handler; the durable outbox holds the envelope until the handler EF commit succeeds. Inline sends before commit leak duplicate user-facing messages on retry. AI-generate-then-send pairings collapse into a single post-commit dispatch handler that does AI gen + send + cleanup-on-AI-null in its own short tx. AI-stage handlers across feedback, chat-routing, taxonomy, matching, intent classification, and funnel-orchestration extract/eta steps are tagged `[NonTransactional]` so `AutoApplyTransactions` does not pin an Npgsql connection across the 60-150s Ollama inference window — orthogonal to `Wolverine.DefaultExecutionTimeout`, which bounds the handler wall-clock independently. `AiReplyHelper.TryGenerateAsync` drops the message on failure (intended for non-critical sends); `AiReplyHelper.TryGenerateOrFallbackAsync` (used by `PresentMatchesHandler`) emits a deterministic plain-text fallback so the user always gets a visible reply. `IdleReminderHandler`, `ProviderRefreshCheckHandler`, `PhoneExchanger`, and `IterationCoordinator` still send inline — slated for follow-up migration; do not regress them by adding new inline sends elsewhere.
- **Aggregate mutations live in Wolverine handlers**: entities that implement `IAggregateRoot` are transactional consistency boundaries; the subset that raise events (`ChatSession`, `ServiceRequest`) extend `AggregateRoot` and call `RaiseDomainEvent(...)` inside state-changing methods. `DomainEventScraper<AggregateRoot, IDomainEvent>` drains the queue at EF `SaveChanges` and enrols envelopes in the durable outbox — **only** inside `AutoApplyTransactions` middleware (i.e. a Wolverine handler context). Hubs, endpoints, background services MUST dispatch a command (`bus.InvokeAsync` / `bus.PublishAsync`) and let a handler own the mutation — never `SaveChanges` an aggregate from outside a handler (covered by `NonHandlerContextEventLossTests`). The previous manual `DequeueEvents()` drain pattern is banned. Orchestrators may still `bus.PublishAsync` directly when no aggregate owns the event.

## Frontend routes

- `/` — landing page (post-WhatsApp link target).
- `/c/:chatId/:token` — ephemeral E2E chat room.
- `/dev` — dev console (WhatsApp simulation).
- `/terms`, `/privacy` — legal pages, lazy-loaded; `RETENTION_DAYS` literal lives in `frontend/src/legal/RetentionDays.ts` and `SUPPORT_WHATSAPP` (digits-only E.164) is driven by `VITE_SUPPORT_WHATSAPP` — contact links render as `https://wa.me/<digits>`.

## Style Principles

- **`string.Empty` over `""`**: prefer `string.Empty` for runtime values. Literal `""` only where C# requires a compile-time constant — default parameter values, positional record defaults, attribute arguments, and `const` declarations.
- **Guard clauses, return early**: invert positive `if (success) { body }` into `if (!success) return; body;` to flatten the happy path. Exception: both branches do real work, or unconditional code follows that needs the value.
- **Avoid nullable primitives and collections; avoid `null` as much as possible**: prefer `string.Empty`, `[]` (empty collection), or `FrozenDictionary<,>.Empty` over `string?` / `List<>?` / `IReadOnlyDictionary<,>?`. Nullable reference types are enabled — let the compiler enforce non-null where the domain allows it. Use `Option`-style return discriminators (e.g. the `ExchangeOutcome` enum) rather than `T?` for "absent vs present" business outcomes.
- **Gambianize**: tests, fixtures, dev-console scenarios, and docs MUST use Gambian phones (`+220…`) and Banjul coordinates (~`13.45, -16.6`). Never `+1…` US numbers or other locales — the product is Gambia-only. Pre-existing `+1…` numbers in integration tests are legacy; do not add new ones.
- **Keep things simple**: less code, less to maintain. Self-explanatory names beat comments. Three similar lines beat a premature abstraction. No half-finished implementations, no error handling for cases that can't happen, no feature flags / backwards-compat shims when you can just change the code.

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
- **Connection string**: `Host=localhost;Port=5432;Database=hook;Username=hook;Password=hook`.
- **Tests**: integration tests provision Postgres + PostGIS via Testcontainers (image `postgis/postgis:16-3.4`). Docker is required to run `dotnet test`.
