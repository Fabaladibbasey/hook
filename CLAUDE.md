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
    features/
      chat/     # ChatRoom, sub-components, useChatHub, chatCrypto (flat — high-touch path, no sub-folders)
      dev/      # DevConsole
      legal/    # LegalLayout, Terms, Privacy, SupportContact, constants
    components/ # LegalFooter (shared — ChatRoom + LandingPage consume)
    api/        # fetchJson (shared HTTP transport)
    LandingPage.tsx  # root route (no folder — single file)
    main.tsx
  vite.config.ts tsconfig.json package.json
```

Path alias `@/*` → `frontend/src/*` (see `tsconfig.json` + `vite.config.ts`). Cross-slice imports use `@/` (`@/components/LegalFooter`, `@/api/fetchJson`). Intra-slice imports stay relative (`./useChatHub`).

Key feature slices live under `backend/src/Features/`:
`Ai`, `ChatLifecycle`, `ChatPrivacyRouting`, `ChatSession`, `ContactSharing`, `Feedback`, `Geocoding`, `Matching`, `MetaTemplates`, `Observability`, `ProviderAvailability`, `RateLimiting`, `ServiceRequest`, `ServiceTaxonomy`, `Whatsapp`.

Wolverine messaging runs in-process. `Wolverine.DefaultExecutionTimeout` (default 60s) preempts long Ollama calls — keep them aligned in `Program.cs`.

## Key Patterns

- **Vertical slices**: each `Features/<Slice>/` owns its handlers, DTOs, persistence calls, and tests.
- **AI provider**: Ollama is mandatory — the dev stub has been removed. `AiReplyHelper` drops the message on failure rather than falling back. `/readyz` pings AI with a 10s cache.
- **No silent listed-provider inbounds**: every inbound that extends a provider's TTL must produce a visible WhatsApp reply.
- **Bot-owned action lines**: in matching flow the bot owns the match action line verbatim — do not let `MatchPresenter` regenerate it.
- **Single-device E2E chat**: SignalR uses one ECDH-derived AES-GCM key per chat between the two participants; server stores a single ciphertext per message. Opening the chat URL on a new device rotates the participant's `CurrentSessionId` (revoking the prior tab on its next hub interaction) AND clears the participant's stored `PublicKey` — the new device must republish a fresh public key, after which the peer re-derives the shared key. `LastInboundSequence` is preserved across rotation so the participant-scoped unique index `ux_chat_messages_chat_participant_sequence` is not collided; the cursor is republished via `OpenChatResponse.OutboundSequenceCursor` and the new device sends from `cursor + 1`. Pre-rotation history persists as ciphertext but renders as undecryptable placeholders on devices without the original keypair in `localStorage`. AAD binds chatId + sender + recipient + messageId + sequence to prevent reflection and junk-injection. Duplicate `messageId` is rejected with `ChatMessageRejectReason.Duplicate`. Two unique constraints back the rejection: the PK on `chat_messages.id` (same-id replay) and `ux_chat_messages_chat_participant_sequence` on `(ChatId, ParticipantId, Sequence)` (two tabs racing the same sequence). Both surface as `Duplicate`; neither inserts a row nor advances the sequence. The hub dispatches `PublishParticipantKeyCommand` and `AcceptChatMessageCommand` via Wolverine `bus.InvokeAsync` — the handlers own the aggregate mutation, SPKI parse-validation, ciphertext insert, sequence advance, session touch, and `ChatParticipant`/`ChatSession` `Version` concurrency-token bumps inside the explicit/`AutoApplyTransactions` commit. The hub is now a thin shape-translator and broadcasts via the outbox-driven `BroadcastChatEvent` raised on `SetPublicKey`/`End`/`HardExpire`/`SendIdleReminder`. `OpenChatEndpoint` likewise dispatches `RotateSessionCommand`; no endpoint or hub method calls `SaveChanges` directly.
- **Cross-flow routing**: providers may register against multiple services; routing is flexible.
- **Geocoding**: all coordinates are Banjul / Gambia — dev defaults, integration test fixtures, and unit-test stubs share the same reference point.
- **Retention sweep**: `Shared/Retention/` runs a `RetentionHostedService` daily. Configured via `Retention:*` in `appsettings.json`. Disabled by default in tests via `b.UseSetting("Retention:Enabled","false")` in `DevPipelineFixture`; tests that exercise the sweeper construct it directly with explicit options.
- **`ExchangeOutcome` contract**: nine values split by terminal vs transient failure mode. `Exchanged`/`RoutedToChat` are fresh successes; `AlreadyShared`/`AlreadyRouted` are idempotent re-picks (per-match notice already sent); `RaceLost` is "lost the atomic claim" (covers consent revocation between read and claim); `ProviderExpired`/`ProviderMissing`/`RequestMissing` are terminal for that match; `InvalidData` is a phone-parse failure. Switch consumers must include a `default` arm.
- **Partial unique index + 23505 catch idiom**: `DbSetExtensions.TryInsertUniqueAsync(db, entity, constraintNames, ct)` wraps the insert + Postgres `23505` catch by constraint name, returning `false` on the unique race. The insert runs inside a savepoint when an outer Wolverine handler tx is present, so a lost race does not poison the enclosing transaction. Constraint names live in per-feature constants (`FeedbackConstants.PendingUniqueIndexName`, `FeedbackConstants.RequestStep1UniqueIndexName`, `MatchConstants.RequestProviderUniqueIndexName`) so the entity config, the migration, and the catch site all reference one source of truth.
- **`TimeProvider` injection**: services that need current time (`MatchingService`, `FeedbackResponseService`, `PhoneExchanger`) accept `TimeProvider` via DI. Tests inject a fixed-time provider; production uses `TimeProvider.System`.
- **`WhatsappContact.UpsertInboundAsync`**: every routed inbound writes the contact's `LastInboundAt` AFTER the cancel/abandon detection — a CANCEL message that tears down a draft must not extend the contact's last-inbound timestamp.
- **Feedback Pillar A / Pillar B**: Step1 ("did you find a provider?") fires on two distinct triggers depending on the routing path. **Contact-share path** (no chat session): the bot schedules `Step1FeedbackCheck` at `Feedback:Step1InitialDelay` (default 30min wall-clock) from `ContactExchangedHandler`. **Chat-route path** (private chat session): Step1 fires event-driven on `ChatSessionEndedEvent` — every end reason (User/Idle/Expired/ProductiveSilence) fans out one `Step1FeedbackCheck` per match associated with the chat via `ChatSessionEndedHandler`. **ProductiveSilence trigger**: while the chat is still Active, `ProductiveSilenceHandler` fires if both sides have exchanged at least `ChatOptions:ProductiveSilenceMinMessagesPerSide` messages AND the chat has been silent for `ProductiveSilenceMinutes` (atomic gate via `TryMarkProductiveSilenceAsync`). **Step1 reschedule ladder**: when the client replies "still looking"/"later" (deterministic or AI-classified `Reschedule`), `Step1RecheckCommand` re-fires through `Step1RecheckHandler` at the rung dictated by `Step1RecheckSchedule[RecheckCount]`. After `Step1MaxRechecks` consecutive ambiguous replies the row is claimed `Skipped`. `MinRecheckGap` debounces back-to-back rechecks (scheduled + opportunistic firing within seconds of each other) at the repo layer via `TryRepromptPendingAsync`. **CaptureNoReason follow-up**: on Step1=No the handler opens a `CaptureNoReason` Pending row and asks "what made it hard?"; free text is persisted to `NoReason`, "SKIP" leaves it null. Phone-number-shaped tokens are scrubbed to `[phone]` before persistence. **Pillar A**: on Step1=Yes single-pick publishes `Step2FeedbackCheck` immediately; multi-pick first runs `IdentifyWinner`. **Pillar B**: on Step2=InProgress with a usable ETA (deterministic relative-duration extract via `QuickIntent.TryExtractRelativeEta` OR AI-extracted via `ExtractStep2IntentCommand`/`ApplyStep2IntentCommand`) the handler claims `EtaCaptured` with the ETA and schedules `Step2FeedbackCheck` directly at `eta + EtaScheduleBuffer`, skipping `AwaitingEta`. With no usable ETA the handler reserves an `AwaitingEta` Pending row and asks the canonical follow-up. ETAs beyond `MaxEtaHorizon` (default 7d) are capped at the horizon for both Step1 Reschedule and Step2 InProgress; the dedicated `AwaitingEta` capture step still treats hallucinated over-horizon ETAs as parse failures. AI-extracted ETAs are gated server-side: `OllamaConversationAi` only honors `etaUtc` when the source text contains a digit run or an ETA keyword. **Outbox-compat rule**: enums on durable-outbox commands MUST pin explicit ordinals; new members append only. `ExtractStep{1,2}IntentCommand` text bodies are PII-scrubbed (`+digits` → `[phone]`) and capped at `FeedbackOptions.OutboxTextMaxChars` (default 1000) at the publish site so dead-letter rows do not accumulate unmasked numbers at rest.
- **Bot-owned multi-pick list**: the `IdentifyWinner` "Which provider worked out? — 1) +220...XX, 2) ..." line is owned by the bot deterministically through `PickedMatchListFormatter`, in `MatchRepository.GetForRequestAsync` production order (`Score DESC, DistanceKm, CreatedAt, Id`) — same enumeration the original `MatchPresenter` `PICK 1/2/3` used, so the client's positional reply binds back to the correct match. Non-Exact rows (Broadened, Narrowed) carry a trailing ` ({MatchLabels.Related})` tag so the client sees the same hierarchy hint that `MatchPresenter` surfaces in the initial match list.
- **Service taxonomy hierarchy**: `Service` is a tree (parent / children via `Service.ParentSlug` self-FK, `OnDelete(SetNull)`). 16 root sectors are seeded at boot by `RootSectorSeeder.EnsureRootSectorsAsync` (idempotent via `TryInsertUniqueAsync` against `PK_services`). New non-root slugs created by `SlugResolver` publish `JudgeParentSlugCommand` → `JudgeParentSlugDispatchHandler` runs Ollama → `bus.InvokeAsync(AssignServiceParentCommand)` → `AssignServiceParentHandler` mutates the aggregate. Matching uses `IServiceRepository.ExpandAsync(slug)` to produce `ExpandedSlugs(Requested, Parent?, Children)`; `MatchKind` records whether a candidate matched the requested slug (`Exact`), the parent the query broadened to (`Broadened`, harsher score discount via `BroadenedMatchFactor`), or a child the query narrowed to (`Narrowed`, modest discount via `NarrowedMatchFactor`).
- **Wolverine durable persistence (always-on)**: Wolverine persists scheduled messages + outbox in the `wolverine` Postgres schema in every environment, including tests. `opts.Policies.UseDurableLocalQueues()` promotes in-process local queues to durable so envelopes published to `[NonTransactional]` handlers (the AI stages) survive a crash between publish and inner-commit. The schema is created by Wolverine itself, **not** by EF migrations — separate lifecycle from the `public` schema. Per-shard isolation relies on each test fixture using its own Postgres database; the `wolverine.*` schema lives inside the same DB and gets torn down with it. The `AutoApplyTransactions` policy + `UseEntityFrameworkCoreTransactions` make every handler run inside an EF transaction with outgoing envelopes written in the same commit. Transport-level dedupe comes from the `wolverine_incoming_envelopes` PK; handler-level idempotency is each slice's responsibility (e.g. `match.ChatId is not null` checks, partial unique indexes on `MatchFeedback`). Schema additions to records persisted in the outbox MUST be backward-compatible — give new positional fields a default value (e.g. `string Reserved = ""`) so envelopes serialised before the deploy continue to deserialise. Dead-letter rows (handler failure past `MaxAttempts`) hold the original JSON body, which may include user text + unmasked phone for AI-stage envelopes — `RetentionSweeper` prunes `wolverine.wolverine_dead_letters` rows past `Retention:DeadLetterRetentionDays` (default 7d) so PII does not accumulate at rest. **Type renames** are as breaking as removals — Wolverine keys envelopes by full CLR type name. Rename a persisted message and in-flight envelopes dead-letter on the next restart, with full message bodies (incl. PII) left in `wolverine_dead_letters` for `DeadLetterRetentionDays`. Mitigate via either `[MessageIdentity("OldFqn", Version = N)]` aliases or a documented pre-deploy outbox drain. While the product is pre-launch and the DB can be recreated freely, renames are safe — once we have real users this discipline becomes mandatory. See `docs/DEPLOY.md`.
- **Post-commit external sends (feedback + chat-routing + matching + funnel orchestration)**: external side effects (WhatsApp HTTP, SignalR broadcasts, AI-generate-then-send pairings) **must** run via the outbox, not inline. Publish `SendWhatsAppTextCommand` / `BroadcastChatEvent` / `Step1PromptDispatchCommand` / `Step2PromptDispatchCommand` / `PresentMatchesCommand` / `ExtractServicesCommand` / `RegistrationExtractServicesCommand` / `ExtractEtaCommand` / `ApplyEtaCommand` / `ExtractStep1IntentCommand` / `ApplyStep1IntentCommand` / `ExtractStep2IntentCommand` / `ApplyStep2IntentCommand` / `Step1RecheckCommand` / `ClassifyInboundIntentCommand` / `RouteClassifiedIntentCommand` / `SendColdReplyCommand` from a Wolverine handler; the durable outbox holds the envelope until the handler EF commit succeeds. Inline sends before commit leak duplicate user-facing messages on retry. AI-generate-then-send pairings collapse into a single post-commit dispatch handler that does AI gen + send + cleanup-on-AI-null in its own short tx. AI-stage handlers across feedback, chat-routing, taxonomy, matching, intent classification, and funnel-orchestration extract/eta steps are tagged `[NonTransactional]` so `AutoApplyTransactions` does not pin an Npgsql connection across the 60-150s Ollama inference window — orthogonal to `Wolverine.DefaultExecutionTimeout`, which bounds the handler wall-clock independently. `AiReplyHelper.TryGenerateAsync` drops the message on failure (intended for non-critical sends); `AiReplyHelper.TryGenerateOrFallbackAsync` (used by `PresentMatchesHandler`) emits a deterministic plain-text fallback so the user always gets a visible reply. `IdleReminderHandler`, `ProviderRefreshCheckHandler`, `PhoneExchanger`, and `IterationCoordinator` still send inline — slated for follow-up migration; do not regress them by adding new inline sends elsewhere.
- **Aggregate mutations live in Wolverine handlers**: entities that implement `IAggregateRoot` are transactional consistency boundaries; the subset that raise events (`ChatSession`, `ServiceRequest`) extend `AggregateRoot` and call `RaiseDomainEvent(...)` inside state-changing methods. `DomainEventScraper<AggregateRoot, IDomainEvent>` drains the queue at EF `SaveChanges` and enrols envelopes in the durable outbox — **only** inside `AutoApplyTransactions` middleware (i.e. a Wolverine handler context). Hubs, endpoints, background services MUST dispatch a command (`bus.InvokeAsync` / `bus.PublishAsync`) and let a handler own the mutation — never `SaveChanges` an aggregate from outside a handler (covered by `NonHandlerContextEventLossTests`). The previous manual `DequeueEvents()` drain pattern is banned. Orchestrators may still `bus.PublishAsync` directly when no aggregate owns the event.
- **Scoped vs factory `HookDbContext`**: the DI-scoped `HookDbContext` is reserved for tracked entities + the Wolverine handler tx. `IDbContextFactory<HookDbContext>` exists ONLY for read-only parallel reads (`PostgresProviderQueryService` branch fan-in, `SlugResolver.ResolveBatchAsync` isolated paths). Adding a concurrent operation on the scoped context throws `InvalidOperationException: A second operation was started…`. `InboundPrefetchRepository` reads sequentially on the scoped context — one connection per inbound, no factory, and the active `ServiceRequest` stays tracked so the router can `.Close()` it inside the handler tx. **One carveout** to the read-only rule: `SlugResolver.ResolveBatchAsync` writes new `Service` rows through the factory context (`ResolveIsolatedAsync` `SaveChangesAsync`) because the resolver fans out per slug for T7 parallelism and serialising through the scoped ctx would kill that win. Safe because `Service` rows are append-only + idempotently re-resolvable, so an orphan from outer-tx rollback re-creates cleanly on retry. **Intra-batch peer guard**: when fanning out two-or-more new slugs across factory contexts, each inner call receives an `IReadOnlySet<string>` of sibling normalized slugs (self pre-removed) and excludes them from `FindSimilarAsync` candidates. Prevents the loser of a commit race from auto-merging into a sibling under Postgres default `READ COMMITTED` isolation. Cross-batch auto-merge (a later batch seeing a prior committed row) still works through the `GetBySlugAsync` short-circuit. Boundary `ResolveBatchAsync` also normalizes + dedupes by canonical slug and caps batch size via `ServiceTaxonomyOptions.MaxBatchSize` (default 16) so two raws that collapse to the same normalized form cannot race the PK insert.
- **Shared `NpgsqlDataSource`**: a single pinned data source (`Program.cs:65-72`) is shared by EF (scoped + factory) and Wolverine outbox. Default `MinPoolSize=5 MaxPoolSize=64` matches `RateLimit:WebhookConcurrencyLimit + 14` headroom for outbox pollers. Any change to either knob must move them together; see `Features/RateLimiting/README.md`.
- **Schema-head guard at boot**: `Program.cs` calls `GetPendingMigrationsAsync` and fails fast on pending migrations instead of auto-applying. Migrations apply out-of-band (`dotnet ef database update` on the deploy host, `.github/workflows/deploy-hetzner.yml`). Test fixtures still apply migrations themselves to keep parity.
- **`[NonTransactional]` generalizes to slow-HTTP**: applied to AI-stage handlers AND `GeocodeAddressDispatchHandler` so EF/Npgsql does not pin a connection across slow outbound HTTP (Ollama inference, Google Geocoding). The transactional `ApplyClientGeocodeResultCommand` / `ApplyProviderGeocodeResultCommand` siblings do the durable state mutation. `GeocodeAddressCommand` carries `DraftStampedAt = draft.UpdatedAt` at publish time; apply handlers discard envelopes whose stamp does not match the current draft (covers CANCEL+restart races inside the geocode round-trip).
- **`JudgeParentDedup` gate**: between `SlugResolver → JudgeParentSlugCommand` and `JudgeParentSlugDispatchHandler` is a 5-min dedup gate (`IJudgeParentDedupGate`) that suppresses re-judging the same slug. The gate writes to `judge_parent_dedup` via single-shot `INSERT … ON CONFLICT DO UPDATE WHERE judged_at <= cutoff RETURNING`. Rows are tiny and currently not swept (low priority).
- **try-catch budget**: sanctioned reasons only — (1) control flow (`23505` race + savepoint in `DbSetExtensions.TryInsertUniqueAsync`, `RootSectorSeeder` cold-boot batch insert + per-slug fallback, `WhatsappClient.TryExtractMessageId` parse-or-null, `ReceiveWebhookEndpoint.HandleInbound` 200-on-malformed-payload to suppress Meta retry), (2) per-iteration isolation (`InboundRouter` per-match, `RetentionSweeper` per-table), (3) graceful-drop with metric (`AiReplyHelper`, `OllamaConversationAi.TryCallAsync` — all increment `HookMetrics.AiOutboundDropped` with a `stage` tag; `ClassifyInboundIntentHandler` also bumps `AiClassifyFailures` for legacy dashboards), (4) compensating action (`RegistrationOrchestrator`, `ClientRequestOrchestrator` — delete draft + send fallback), (5) distributed lock acquire/release, (6) SSE client-disconnect, (7) hosted-service shutdown + warmup/probe state-cache (`AiWarmupHostedService`, `AiReadinessProbe`, `WolverineDlqIndexBootstrap` 42P01/3F000 soft-fail), (8) top-level Serilog bootstrap (`Program.cs` `Log.Fatal` + rethrow). **Forbidden:** log-and-rethrow, log-and-swallow without metric, defensive catch around a single happy-path call. Rely on platform extensions instead: HTTP → `GlobalExceptionHandler : IExceptionHandler` (`Shared/Core/`) — Production logs DO attach the exception object (self-hosted Seq + stdout are operator-controlled; redaction lives at the response surface — `Detail` never echoes `exception.Message`, the `queryString` extension is gone, only `traceId`/`activityId`/`method`/`path` ship), `ProblemDetails.Instance` excludes query-string, `MapStatus` walks `InnerException` AND `AggregateException.Flatten()` so EF-wrapped + parallel-fan-in 23505 still map to 409; Wolverine → `opts.Policies.OnException<>` (`Program.cs` `UseWolverine` block) — OCE-Discard is gated on `WolverineShutdownGate.IsStopping` (armed by `IHostApplicationLifetime.ApplicationStopping`; handler-local OCEs still surface as DLQ rows), transient sql-states via `Shared/Messaging/TransientPgStates.IsTransientFast`/`IsTransientSlow` (both walk InnerException so EF's `DbUpdateException(PostgresException)` wrap retries) — fast tier (deadlock/serialization) uses sub-second cooldowns, slow tier (connection storm) keeps multi-second cooldowns; no `HttpRequestException` policy (Polly `AddStandardResilienceHandler` already retries at the `HttpClient` layer); SignalR → `ChatHubExceptionFilter : IHubFilter` (`Features/ChatSession/`) covering `InvokeMethodAsync` + `OnConnectedAsync` + `OnDisconnectedAsync` — rethrows `HubException` (client-facing), swallows OCE only when `ConnectionAborted` (on all three methods), `OnDisconnectedAsync` swallows by design (SignalR ignores its exceptions), otherwise increments `hook.chat_hub.faults` with a `method` tag; `IConversationAi` non-absorbing `PingAsync` is the dedicated probe surface for `AiReadinessProbe` + warmup so absorbed-fallback no longer fakes healthy. **Inline-send handlers (`IdleReminderHandler`, `ProviderRefreshCheckHandler`, `PhoneExchanger`, `IterationCoordinator`) are hot for duplicate sends under the new transient-PG retry policy; do not add new inline sends — use post-commit publish like the rest of the codebase. Migration of the existing four is a follow-up.**

## Frontend routes

- `/` — landing page (post-WhatsApp link target).
- `/c/:chatId/:token` — ephemeral E2E chat room.
- `/dev` — dev console (WhatsApp simulation).
- `/terms`, `/privacy` — legal pages, lazy-loaded; `RETENTION_DAYS` + `LEGAL_EFFECTIVE_DATE` live in `frontend/src/features/legal/constants.ts` and `SUPPORT_WHATSAPP` (digits-only E.164) is driven by `VITE_SUPPORT_WHATSAPP` — contact links render as `https://wa.me/<digits>`.

## Style Principles

- **`string.Empty` over `""`**: prefer `string.Empty` for runtime values. Literal `""` only where C# requires a compile-time constant — default parameter values, positional record defaults, attribute arguments, and `const` declarations.
- **Guard clauses, return early**: invert positive `if (success) { body }` into `if (!success) return; body;` to flatten the happy path. Exception: both branches do real work, or unconditional code follows that needs the value.
- **Avoid nullable primitives and collections; avoid `null` as much as possible**: prefer `string.Empty`, `[]` (empty collection), or `FrozenDictionary<,>.Empty` over `string?` / `List<>?` / `IReadOnlyDictionary<,>?`. Nullable reference types are enabled — let the compiler enforce non-null where the domain allows it. Use `Option`-style return discriminators (e.g. the `ExchangeOutcome` enum) rather than `T?` for "absent vs present" business outcomes.
- **Gambianize fixtures**: The Gambia is the default seed market — tests, fixtures, dev-console scenarios, and internal docs MUST use Gambian phones (`+220…`) and Banjul coordinates (~`13.45, -16.6`). Never `+1…` US numbers or other locales in fixtures. Pre-existing `+1…` numbers in integration tests are legacy; do not add new ones. **Product is global** — user-facing copy (landing page, marketing surfaces) must stay locale-neutral; do not bake Gambia into UI strings.
- **Keep things simple**: less code, less to maintain. Self-explanatory names beat comments. Three similar lines beat a premature abstraction. No half-finished implementations, no error handling for cases that can't happen, no feature flags / backwards-compat shims when you can just change the code.

## Change Discipline
- **Do not change anything without a defensible reason.** Before every edit, ask: is the change *necessary*? Does it add *business value*, *clarity*, *consistency*, or *simplification*? If none apply, do not make the change.
- **Validate the reason for each change before applying it** — not in bulk, but per edit. A plan that justifies one change does not justify the next.
- **Do not change for the sake of changing.** No drive-by refactors, no renames-by-fashion, no "while I'm here" edits.
- **Do not touch unrelated elements.** If it's outside the stated scope of the task, leave it alone.
- When you do change something, be prepared to defend the reason in plain terms when asked.

## Naming Conventions

- C#: PascalCase types, `_camelCase` private fields, async methods end in `Async`.
- Test projects: `Hook.<Kind>Tests` (e.g., `Hook.UnitTests`, `Hook.IntegrationTests`).
- Test classes: `<TypeUnderTest>Tests`, methods `Method_State_Expected`.
- Feature folders: PascalCase singular noun (`Matching`, not `Matchings`).
- TypeScript: PascalCase components, camelCase hooks/utils, `*.tsx` for JSX, `*.ts` otherwise.
- Configuration files: `appsettings.<Env>.json`, never commit `appsettings.Development.json` (already gitignored).

## Naming conventions for contract types

Role-explicit suffixes make the role of every type self-evident at the call site.

- **Wolverine imperative-dispatch messages** use `*Command` suffix (`SendWhatsAppTextCommand`, `RouteMatchToChatCommand`, `RouteInboundMessageCommand`). All in-process messages — including Wolverine fan-out to multiple handlers — use `*Command`.
- **`*Event` suffix is reserved** for types that (a) implement `IDomainEvent` AND (b) are raised by an `AggregateRoot` via `RaiseDomainEvent` (drained by `DomainEventScraper`). Wolverine multi-handler dispatch works on commands — use `*Command`, not `*Event`, for fan-out.
- **One allowed acronym exception**: payload-noun "Event" tokens where "Event" is part of the payload's domain name (e.g. `BroadcastChatEvent` carries a SignalR-side chat-event payload). These are still domain events under rule (a)+(b); the "Event" suffix coincides with the payload noun.
- **Pure past-tense domain events** (`X happened`) use `*Event` suffix (`ContactExchangedEvent`, `ServiceRequestCreatedEvent`) only when they satisfy both conditions above.
- **Wolverine handler return values** (`bus.InvokeAsync<T>`) use `*Response` suffix (`EndChatResponse`).
- **HTTP endpoint request bodies** use `*Request`; response bodies use `*Response` (`OpenChatResponse`, `InjectInboundRequest`, `EchoOutboundRequest`).
- **Scheduled recurring dispatches** keep `*Check` (`Step1FeedbackCheck`, `IdleReminderCheck`).
- **Service-method return values** keep `*Result` (`GeocodeResult`, `ResolveSlugResult`); business-decision enums/records keep `*Outcome` (`ExchangeOutcome`, `RateLimitOutcome`).
- **Handler classes** use `*Handler` (no role suffix duplicated — the pattern is `<MessageMinusSuffix>Handler`, so `SendWhatsAppTextCommand` is handled by `SendWhatsAppTextHandler`).
- **Frontend TS** uses the same nouns without `Dto` suffix; align type names cross-stack where they describe the same wire shape (`EncryptedChatMessage`, `ChatMessageRejected`, `OpenChatResponse`).

## Environment & CI

- **Platform**: Windows 11 dev box. Shell is bash (Git Bash); PowerShell 7+ also available. Use forward slashes and `/dev/null` (not `NUL`). CI runs Ubuntu.
- **Runtime versions**: .NET 10 SDK (`10.0.x`), Node 24, Postgres 16 + PostGIS 3.4 (CI uses `postgis/postgis:16-3.4`).
- **Dev runtime**: backend at `:5212`, vite at `:5173`/`:5174`, Postgres at `:5432`.
- **Build & Test workflow**: `.github/workflows/build-and-test.yml` — restores, builds Release, runs tests, smoke-publishes a container. `working-directory` defaults to `backend/`.
- **Deploy**: `.github/workflows/deploy.yml`. For deploy sequencing, outbox-drain requirements, and Wolverine message-rename hazards, see `docs/DEPLOY.md`.
- **Secrets**: never commit `.env*`, `appsettings.Development.json`, `*.pem`, `*.key`, user secrets. `.gitignore` already covers these.
- **Connection string**: `Host=localhost;Port=5432;Database=hook;Username=hook;Password=hook`.
- **Tests**: integration tests provision Postgres + PostGIS via Testcontainers (image `postgis/postgis:16-3.4`). Docker is required to run `dotnet test`.
