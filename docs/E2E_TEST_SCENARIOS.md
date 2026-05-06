# Hook — End-to-End Test Scenarios

Durable catalog of every E2E scenario for the Hook system. Run any subset; mark each pass/fail per run. Date-stamped run reports (e.g. `E2E_TEST_REPORT_YYYYMMDD.md`) reference scenarios by ID from this file.

---

## Setup (read once before any run)

### Modes

| Mode | Config | Inbound source | Outbound capture |
|---|---|---|---|
| **dev** | `Dev:Whatsapp:Enabled=true` (overrides `WhatsappClient` with `FakeWhatsappClient` + `DevOutbox`) | `POST /dev/whatsapp/inbound` | `GET /dev/whatsapp/outbox/stream` (SSE) |
| **real** | `Dev:Whatsapp:Enabled=false`, ngrok forwarding `https://<ngrok>/webhooks/whatsapp` → `:5212` | WhatsApp Web bot conversation | WA Web bot reply bubble |

### Ports / processes (per `memory/project_dev_runtime_setup.md`)

- Backend: `:5212`
- Frontend (vite): `:5173` / `:5174`
- Postgres: `:5432`, db `hook`
- Ollama: `:11434`, model `qwen2.5:3b`

### Real-mode actors

- Bot test number: `+1 (555) 633-2510` (Meta test app)
- Sender pool (WA Web logged-in accounts) — **all interchangeable**, any can act as client or provider in any scenario:
  - `+2203539005`
  - `+2207019331`
  - `+2206784709`

  Per-run convention: write down which number played which role at the top of the run report so results are reproducible.
- Webhook URL: ngrok HTTPS `→ http://localhost:5212/webhooks/whatsapp`
- App secret + verify token + access token: `appsettings.Development.json` (gitignored). Keys: `Whatsapp:AppSecret`, `Whatsapp:VerifyToken`, `Whatsapp:AccessToken`, `Whatsapp:PhoneNumberId`. Rotate access token via Meta dev console; paste into `appsettings.Development.json` and restart backend.

#### Multi-party scenarios newly runnable in real mode

With 3 numbers available, these scenarios — previously dev-only or impractical — become real-mode-feasible:

| Scenario | New real-mode setup |
|---|---|
| CN-007 PICK ALL share=true | Pick any 2 numbers as providers (share=true, same service). Third sends a matching client request. After `PICK ALL`, both providers should receive `"Client wants <slug> (<client>). Expect a message."` |
| CN-008 PICK 1,3 multi-select | Same setup — client picks specific providers by index. Only picked providers receive notification. |
| CN-001 PICK with share=true | Provider phone shown to client is a real WA number → can be messaged directly. |
| CN-003 PICK with share=false → chat link | Both client and provider receive `/c/.../<token>` via real WA; open in two browser tabs to drive CH-* end-to-end without DB seeding. |
| CH-004/005 SignalR key exchange + send | Open both chat-link URLs in two browser tabs (or two devices); covers full E2E crypto path with real participants. |
| FB-001 Step1 prompt | Client number replies YES/NO on real WA; verify `MatchFeedback` row + `ProviderStats` updates. |

### Dev seeding

- `Dev:Providers:Enabled=true` + `AutoSeed=true` → `DevProviderSeeder` populates fixtures on startup.
- Manual seed/reseed: `POST /dev/providers/seed`, `DELETE /dev/providers/{phone}`.
- Test coordinates: SF `(37.7749, -122.4194)`. Dev coordinates: Banjul `(13.4549, -16.5790)`. Cross-coords scenarios use this split.

### Time-driven flows (4h / 20h / 22h / 23h / 24h)

System has scheduled events: `Step1FeedbackCheck` (+4h or +23h), `Step2FeedbackCheck` (+20h or +48h), `IdleReminderCheck` (+20m), `IdleEndCheck` (+30m), hard-expire (+24h), `ProviderRefreshCheck` (+22h).

**Both modes:** publish the scheduled event directly via `IMessageBus.PublishAsync` instead of waiting. Two options:

1. **Preferred — add temporary dev endpoint.** Wire `POST /dev/schedule/fire { kind, payload }` for the duration of the test session; remove after. **This endpoint is not yet implemented — add it as a prerequisite for any FB-/CH-idle-/PR-refresh scenario.**
2. **Workaround — psql + bus seam.** Insert/mutate the relevant row directly, then trigger via a one-shot xUnit integration test that resolves `IMessageBus` from the running host's container.

Document which option used per run.

### chrome-devtools MCP SOP (real mode only)

1. **Profile lock:** before `mcp__chrome-devtools__list_pages`, kill any Chrome process attached to the MCP profile dir (`Stop-Process -Name chrome -Force` — needs user permission).
2. **Composer auto-clear quirk:** WA Web clears `contenteditable` on focus. After `fill` or `type_text`, send `press_key Enter` immediately. Never type twice without an Enter between — text doubles.
3. **Meta latency:** webhook → orchestrator → outbound is 5–12 s round-trip. Use `wait_for` with timeout ≥ 15 s for every reply assertion.
4. **Native location picker NOT driveable:** scenarios needing GPS pin (PR-006, CL-003) marked `[real-manual]` — must be done with a real phone, not MCP.
5. **Take snapshot for assertions:** `mcp__chrome-devtools__take_snapshot` on the chat panel; assert outbound text via the latest reply bubble's text node.

### Severity legend

- **P0** — smoke. Must pass on any branch before merge. Target: ~17 scenarios, < 15 min total in dev mode.
- **P1** — critical. Defect regressions, privacy/security paths, cross-flow guards.
- **P2** — exhaustive. Every branch, error path, race, language variant.

### Mode legend

- `[both]` — runs in dev and real.
- `[dev]` — dev-only (cannot be reproduced via real WA — bad signature, malformed JSON, oversized payload, AI failure injection).
- `[real]` — real-only (WA-Web composer behavior, native pickers).
- `[real-manual]` — real-only, requires a real phone (not chrome-devtools MCP).

### Per-scenario shape

```
### ID-NNN — Title  [P0|P1|P2]  [both|dev|real|real-manual]

Preconditions:
- DB / config / clock state

Expected:
- Outbound 1: <exact text or regex>
- DB: <table.column = value>
- Log: "<log substring>"
- Event: <EventName> published

Dev exec:
1. ...

Non-dev exec (chrome-devtools MCP):
1. ...

Linked defect: D{1..6} | none
Notes: ...
```

---

## Results matrix template

Copy into per-run report. Fill `result` column.

| ID | Title | Mode run | Result | Notes |
|---|---|---|---|---|
| WHK-001 | … | dev | | |
| … | … | … | | |

---

## § 1. Webhook & infra prerequisites

### WHK-001 — GET verify with correct token  [P0] [both]

**Preconditions:** `Whatsapp:VerifyToken=<known>`, backend up.
**Expected:** HTTP 200, body = challenge string. Log `"WhatsApp webhook verification succeeded"`.
**Dev exec:** `curl "http://localhost:5212/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=<token>&hub.challenge=abc123"` → `abc123`.
**Non-dev exec:** in Meta dev console, click "Verify and save" on webhook config; expect green check.
**Linked defect:** none.

### WHK-002 — GET verify with wrong token  [P1] [dev]

**Preconditions:** any.
**Expected:** HTTP 403. Log `"WhatsApp webhook verification failed (mode=subscribe)"`.
**Dev exec:** `curl -i "http://localhost:5212/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=WRONG&hub.challenge=x"` → 403.
**Non-dev exec:** N/A — Meta only sends correct token.
**Linked defect:** none.

### WHK-003 — POST inbound with valid HMAC signature  [P0] [both]

**Preconditions:** `Whatsapp:AppSecret` set; payload is a valid Meta v17 webhook envelope with one text message.
**Expected:** HTTP 200. Log `"Inbound WhatsApp message {wamid} from +XXXX kind=Text"`. `InboundMessageReceived` published on `IMessageBus`.
**Dev exec:** craft body, compute `sha256=<hex>` with secret, `POST /webhooks/whatsapp` with header `X-Hub-Signature-256`. Use a fixture script.
**Non-dev exec:** send any text from `+2203539005` to bot; verify via backend logs `Inbound WhatsApp message wamid.…`.
**Linked defect:** none.

### WHK-004 — POST inbound with invalid signature  [P1] [dev]

**Preconditions:** `Whatsapp:AppSecret` set.
**Expected:** HTTP 403. Log `"WhatsApp webhook signature validation failed"`. **No** `InboundMessageReceived` published.
**Dev exec:** valid body, `X-Hub-Signature-256: sha256=000…` (bogus) → 403.
**Non-dev exec:** N/A — Meta always signs correctly.
**Linked defect:** none.

### WHK-005 — POST inbound with missing signature header  [P1] [dev]

**Preconditions:** as above.
**Expected:** HTTP 403.
**Dev exec:** POST without `X-Hub-Signature-256` header → 403.
**Non-dev exec:** N/A.
**Linked defect:** none.

### WHK-006 — Duplicate MessageId dedup  [P1] [both]

**Preconditions:** `MemoryInboundDedup` is the registered `IInboundDedup`.
**Expected:** first POST processes; second POST with same `wamid.X` is skipped. Log on second: `"Skipping duplicate WhatsApp message wamid.X"`. Outbox emits one outbound only.
**Dev exec:** `POST /dev/whatsapp/inbound { from, text, messageId: "wamid.dup.test" }` twice. Second returns HTTP 409 `{ "error": "duplicate" }`. Outbox stream shows one outbound.
**Non-dev exec:** Meta retries same wamid on its own under high latency — observe via real WA send → multiple inbound logs but one outbound. Hard to force; usually witnessed organically.
**Linked defect:** none.

### WHK-007 — Malformed JSON body  [P2] [dev]

**Preconditions:** any.
**Expected:** HTTP 200 (Meta requires 200 to avoid retries). Log `"Malformed WhatsApp webhook payload"`. No `InboundMessageReceived`.
**Dev exec:** POST `not-json{` with valid signature for those bytes → 200.
**Non-dev exec:** N/A.
**Linked defect:** none.

### WHK-008 — `/healthz` returns ok  [P0] [both]

**Expected:** HTTP 200, body `{"status":"ok"}`.
**Dev exec:** `curl http://localhost:5212/healthz`.
**Non-dev exec:** same — environment-agnostic.

### WHK-009 — `/readyz` with AI up  [P1] [both]

**Preconditions:** Ollama up at `:11434`, model loaded warm.
**Expected:** HTTP 200, `ok=true`.
**Dev exec:** `curl http://localhost:5212/readyz`.
**Non-dev exec:** same.
**Notes:** known D6 — probe times out at 2s on cold CPU; warm model first. Tune `AiReadinessProbe.ProbeTimeout` if env can't meet 2 s.

### WHK-010 — `/readyz` with AI down  [P1] [dev]

**Preconditions:** stop Ollama.
**Expected:** HTTP 503, `ok=false`, `error` populated.
**Dev exec:** `pkill ollama; curl -i http://localhost:5212/readyz`.
**Non-dev exec:** N/A.

### WHK-011 — `/metrics` (Prometheus scrape)  [P2] [dev]

**Expected:** HTTP 200, `text/plain` body containing `process_cpu_seconds_total`, `dotnet_collection_count_total`, custom counters from `Observability`.
**Dev exec:** `curl http://localhost:5212/metrics | head -50`.
**Non-dev exec:** N/A.

### WHK-012 — Correlation ID echo  [P2] [dev]

**Preconditions:** `CorrelationIdMiddleware` registered.
**Expected:** request with `X-Correlation-Id: abc123` → response includes same header. Logs include `CorrelationId=abc123`.
**Dev exec:** `curl -i -H "X-Correlation-Id: abc123" http://localhost:5212/healthz`.
**Non-dev exec:** N/A.

---

## § 2. Provider journey

### PR-001 — Provider registration full happy path  [P0] [both]

**Preconditions:** no `provider_availabilities` row for sender; no active draft.
**Expected:** 5-step funnel:
1. Inbound `"I offer plumbing"` → outbound `"I detected: plumbing. Reply YES to confirm or EDIT to change."`
2. `"yes"` → `"Send your location pin (or type your address)."`
3. `"123 Main St, ..."` (text address) → `"Found: '<formatted>'. Reply YES to confirm or send your GPS pin instead."`
4. `"yes"` → `"Share your phone with clients on match? Reply YES to share, NO to keep it private."`
5. `"yes"` → `"You are listed for 24h. Reply 'I offer …' anytime to update your services, or LEAVE to unlist."`

DB: row in `provider_availabilities` with `Phone=<sender>`, `Services={"plumbing"}`, `ShareContact=true`, `ExpiresAt=now+24h`.
Log: `"Route → RegistrationOrchestrator"` on each step.
**Dev exec:** sequence of 5 `POST /dev/whatsapp/inbound`; assert each outbound from SSE.
**Non-dev exec:** chrome-devtools MCP types each line; `wait_for` reply; `take_snapshot` to assert.

### PR-002 — Edit branch  [P1] [both]

**Preconditions:** PR-001 step 1 reached (`ConfirmServices` step).
**Expected:** `"edit"` → `"Send the corrected list of services in one message."`. Then `"carpentry and painting"` replaces `DraftServices` and re-prompts `"I detected: carpentry, painting. Reply YES to confirm or EDIT to change."`.
**Dev exec:** as PR-001 first 2 steps with `"edit"` instead of `"yes"`.
**Non-dev exec:** same via WA Web.

### PR-003 — Share=false consent  [P0] [both]

**Preconditions:** as PR-001 through step 4.
**Expected:** at AwaitingConsent reply `"no"` → `"You are listed for 24h. ..."`. DB row has `ShareContact=false`.
**Dev exec:** as PR-001 with final `"no"`.
**Non-dev exec:** same.

### PR-004 — Multi-service extract  [P1] [both]

**Preconditions:** none.
**Expected:** `"I fix doors and repair laptops"` → AI extracts `["carpentry","computer-repair"]` (or similar) → outbound lists both. DB `DraftServices` has 2 entries after `yes`.
**Dev exec:** single `POST /dev/whatsapp/inbound { text: "I fix doors and repair laptops" }`; assert outbound contains both slugs.
**Non-dev exec:** type same line.
**Notes:** AI variability — assertion is "contains 2+ slugs", not exact wording.

### PR-005 — Cap at 5 services  [P1] [both]

**Preconditions:** none.
**Expected:** message with 7 distinct services → outbound starts `"Max 5 services per provider. Keeping: <5 slugs>. Reply YES or EDIT."` `DraftServices` has 5 entries after persist.
**Dev exec:** `POST` with text listing 7 services; assert prefix.
**Non-dev exec:** same.

### PR-006 — GPS pin (location attachment)  [P1] [real-manual]

**Preconditions:** at AwaitingLocation step.
**Expected:** location-kind inbound with lat/lng → `DraftLatitude/DraftLongitude` populated; advance to `AwaitingConsent` (skips `ConfirmLocation`). Outbound `"Got your location. Share your phone..."`.
**Dev exec:** `POST /dev/whatsapp/inbound { from, type: "location", latitude, longitude }`.
**Non-dev exec:** real phone only — WA Web's attach → location not driveable from chrome-devtools MCP. Use a real device once per release.

### PR-007 — Geocode confirm path  [P0] [both]

**Preconditions:** at `AwaitingLocation`. `Geocoding:Provider=google` with key set, OR cache pre-warmed for the test address.
**Expected:** text address → `"Found: '<formatted>'. Reply YES to confirm or send your GPS pin instead."` → reply `"yes"` → advance to `AwaitingConsent`.
**Dev exec:** standard.
**Non-dev exec:** same.
**Linked defect:** D2 (geocoder always returns SF when key absent).

### PR-008 — Geocode failure → request pin  [P1] [both]

**Preconditions:** geocoder returns null (e.g. key absent and no cache, or unparseable input like `"asdfqwer"`).
**Expected:** `"Couldn't find that address. Please send your GPS pin (📎 → Location)."`. Step stays at `AwaitingLocation`.
**Dev exec:** standard.
**Non-dev exec:** same.

### PR-009 — Listed-provider heartbeat (any text)  [P0] [both]

**Preconditions:** sender has active `provider_availabilities` row.
**Expected:** any inbound text routes via `RegistrationOrchestrator` heartbeat path → `ExpiresAt` extended by 24h. Outbound `"You're listed as a provider for <services> (extended for 24h). To request a different service yourself, send 'I need …'. Reply LEAVE to unlist."`.
**Dev exec:** seed provider via `/dev/providers`; send `"thanks"`.
**Non-dev exec:** same.

### PR-010 — Greeting heartbeat for listed provider  [P1] [both]

**Preconditions:** sender listed.
**Expected:** `"hello"` from listed provider → `RegistrationOrchestrator` heartbeat (silent extend), **NOT** cold-reply greeting. (Router check: `providers.GetAsync(phone) is not null` short-circuits the `Greeting/Unknown` cold path.)
**Dev exec:** as PR-009 with `"hello"`.
**Non-dev exec:** same.

### PR-011 — Refresh prompt at +22h  [P1] [both]

**Preconditions:** seeded provider whose `ExpiresAt` is 2h from now (i.e. registered 22h ago).
**Expected:** `ProviderRefreshCheck` published → outbound `"Still available for <services>?"` (template path; verify `Whatsapp:WindowAware` outbound rules). DB unchanged until reply.
**Dev exec:** publish `ProviderRefreshCheck { phone }` via `/dev/schedule/fire` (prerequisite endpoint).
**Non-dev exec:** same — both modes use direct event injection per Setup §.

### PR-012 — Refresh YES extends 24h  [P1] [both]

**Preconditions:** PR-011 ran; outbound prompt sent.
**Expected:** reply `"yes"` → `ExpiresAt` reset to now + 24h.
**Dev exec / Non-dev exec:** standard reply path.

### PR-013 — Refresh no reply → expires  [P1] [both]

**Preconditions:** PR-011 ran; no reply.
**Expected:** at `ExpiresAt`, provider no longer matched. Match candidate query excludes expired.
**Dev exec:** advance `ExpiresAt` via psql `update provider_availabilities set "ExpiresAt"=now() - interval '1 minute'`. Run a new client request matching that service. Assert provider absent from `MatchPresenter` output.
**Non-dev exec:** same.

### PR-014 — Cancel during draft  [P1] [both]

**Preconditions:** active registration draft at any step.
**Expected:** `"end"` / `"bye"` / `"quit"` / `"goodbye"` / `"exit"` / `"leave"` / `"done"` → draft deleted, outbound `"Session ended. Send a new message to start over."`.
**Dev exec:** standard.
**Non-dev exec:** same.

### PR-015 — Cancel literal does NOT abort  [P2] [both]

**Preconditions:** active draft.
**Expected:** `"cancel"` → `QuickIntent` returns `Rejection`, NOT `Cancel`. At `ConfirmServices`, prompts `"Reply YES to confirm or EDIT to change."` (silent reprompt). **D5 regression test** — flips to PASS only when D5 is fixed.
**Dev exec:** standard.
**Non-dev exec:** same.
**Linked defect:** D5.

### PR-016 — Same-service dual-role guard  [P1] [both]

**Preconditions:** sender has open `service_requests` row for `plumbing` (Status=Open or Matched).
**Expected:** at `AwaitingConsent` for plumbing registration → `"You have an open request for plumbing. Cancel it first (reply LEAVE) before listing yourself for plumbing."`. Draft deleted; no provider row written.
**Dev exec:** seed open request via `POST /dev/clients/...` (or run client funnel first); then run provider funnel; reply YES at consent.
**Non-dev exec:** same flow via WA Web.

### PR-017 — Wrong-step recovery  [P1] [both]

**Preconditions:** at `ConfirmServices`.
**Expected:** unrelated text e.g. `"blue cheese"` → silent reprompt `"Reply YES to confirm or EDIT to change."`. Step unchanged. No outbound about cheese.
**Dev exec:** standard.
**Non-dev exec:** same.

### PR-018 — Disambiguation (low confidence)  [P1] [both]

**Preconditions:** no active draft. Inbound text where AI returns `ProviderRegistration` with `Confidence < 0.6`.
**Expected:** `AmbiguousIntentDraft` upserted. Outbound `"Quick check: do you want to HIRE someone (you need a service) or OFFER your services (you provide one)? Reply HIRE or OFFER."`.
**Dev exec:** craft borderline text — e.g. `"i might do something soon"`; OR force via test seam with mocked `IConversationAi`.
**Non-dev exec:** same — AI live; assertion is the disambig prompt arrives.

### PR-019 — Disambiguation reply OFFER  [P1] [both]

**Preconditions:** PR-018 ran; pending `AmbiguousIntentDraft` exists.
**Expected:** reply `"offer"` (or `"2"` / `"provider"` / etc.) → original message replayed into `RegistrationOrchestrator`. Draft deleted.
**Dev exec:** PR-018 then `POST inbound { text: "offer" }`.
**Non-dev exec:** same.

### PR-020 — Disambiguation TTL expires  [P2] [both]

**Preconditions:** ambiguous draft older than 5 minutes.
**Expected:** new inbound treated as fresh classification (draft deleted, NO disambig prompt re-sent for the stale state).
**Dev exec:** seed draft with `CreatedAt = now - 6 minutes` via psql; send any inbound.
**Non-dev exec:** wait 5 minutes after PR-018 disambig prompt; send a new message.

---

## § 3. Client journey

### CL-001 — Client request full happy path  [P0] [both]

**Preconditions:** no active client draft for sender.
**Expected:**
1. `"I need a plumber"` → `"Do you need plumbing? Reply YES or NO — YES to confirm, NO to choose another service."`
2. `"yes"` → `"Send your location pin (or type your address)."`
3. text address → `"Found: '<addr>'. Reply YES to confirm or send your GPS pin."`
4. `"yes"` → `"Got your location. Want to add a description? Send it now or reply SKIP."`
5. `"SKIP"` → `"Should we share your phone number with selected providers? Reply YES or NO."`
6. `"NO"` → `"Looking for nearby providers…"` and `service_requests` row created. `ServiceRequestCreated` event published.

DB: `service_requests` with `ClientPhone=<sender>`, `ServiceSlug="plumbing"`, `Status=Open`, `CurrentRadiusKm=5`, `SharePhoneNumber=false`.
**Dev exec:** standard 6 inbound calls.
**Non-dev exec:** same via WA Web.

### CL-002 — Reject service slug  [P1] [both]

**Preconditions:** at `ConfirmService` step.
**Expected:** `"no"` → `"What service do you need?"`, step rolls back to `AwaitingService`, `DraftServiceSlug` cleared.
**Dev exec:** CL-001 step 1 then `"no"`.
**Non-dev exec:** same.

### CL-003 — GPS pin during request  [P1] [real-manual]

**Preconditions:** at `AwaitingLocation`.
**Expected:** location inbound advances to `AwaitingDescription`, skips `ConfirmLocation`. Outbound `"Got your location. Want to add a short description? Send it now or reply SKIP."`.
**Dev exec:** `POST /dev/whatsapp/inbound { type: "location", latitude, longitude }`.
**Non-dev exec:** real phone (MCP can't drive native picker).

### CL-004 — Description text vs SKIP  [P0] [both]

**Preconditions:** at `AwaitingDescription`.
**Expected:**
- text `"leak under kitchen sink"` → `Description` saved on request row.
- `"SKIP"` / `"no thanks"` / `"all good"` / `"continue"` / yes / no (`IsSkipDescription` returns true) → `Description=null`.
Both finalize the request and publish `ServiceRequestCreated`.
**Dev exec:** two sub-runs.
**Non-dev exec:** same.

### CL-005 — Same-service dual-role block  [P1] [both]

**Preconditions:** sender is listed plumbing provider.
**Expected:** at `AwaitingDescription` for plumbing client request → request rejected → `"You're listed as a plumbing provider. To request plumbing instead, reply LEAVE to unlist first, then send your request again."`. Draft deleted.
**Dev exec:** seed provider for plumbing then run client funnel for plumbing.
**Non-dev exec:** same.

### CL-006 — Different-service dual-role allowed  [P2] [both]

**Preconditions:** sender is listed plumbing provider; requesting carpentry.
**Expected:** request finalizes normally (no block).
**Dev exec:** standard.
**Non-dev exec:** same.

### CL-007 — Disambiguation low-conf for client  [P1] [both]

**Preconditions:** as PR-018 but AI returns `ServiceRequest` low-conf.
**Expected:** disambig prompt fires. Reply `"hire"` → replays into `ClientRequestOrchestrator`.
**Dev exec / Non-dev exec:** standard.

### CL-008 — Cancel during client draft  [P1] [both]

**Preconditions:** active client draft.
**Expected:** `"end"` etc. → draft deleted, `"Session ended. Send a new message to start over."`.
**Dev exec / Non-dev exec:** standard.

### CL-009 — Wrong-step recovery on ConfirmService  [P2] [both]

**Preconditions:** at `ConfirmService`.
**Expected:** unrelated text → `"Please reply YES or NO — YES to confirm <slug>, NO to choose another service."`. Step unchanged.
**Dev exec / Non-dev exec:** standard.

### CL-010 — Geocode failure during client request  [P1] [both]

**Preconditions:** geocoder returns null.
**Expected:** `"Couldn't find that address. Please send your GPS pin (📎 → Location)."`. Step stays at `AwaitingLocation`.
**Dev exec / Non-dev exec:** standard.

### CL-011 — Phone share YES at intake  [P1] [both]

**Preconditions:** at `AwaitingPhoneShareConsent` (reached via CL-001 steps 1–5).
**Expected:** `"YES"` → `"Looking for nearby providers…"`. DB: `service_requests.SharePhoneNumber=true`.
**Dev exec:** POST inbound `{ text: "YES" }` at the consent step.
**Non-dev exec:** same via WA Web.

### CL-012 — Wrong-step recovery at AwaitingPhoneShareConsent  [P2] [both]

**Preconditions:** at `AwaitingPhoneShareConsent`.
**Expected:** unrecognised text (e.g. `"maybe"`) → reprompt `"Should we share your phone number with selected providers? Reply YES or NO."`. Step unchanged. No `service_requests` row created.
**Dev exec / Non-dev exec:** standard.

---

## § 4. Matching & iteration

### MA-001 — ServiceRequestCreated → present top-N  [P0] [both]

**Preconditions:** ≥ 3 active providers seeded for service `plumbing` within 5 km of client.
**Expected:** `ServiceRequestCreatedHandler` runs → `MatchingService.RunForRequestAsync` → `MatchPresenter` outbound listing top 1–5 with format `"<n>. <masked-phone> — <km>km away"` (or AI-rephrased equivalent), tail line `"Reply PICK 1 to PICK <N> to share contact, NEXT for more, or NEW for a different service."`.
**Dev exec:** seed 3+ providers via `/dev/providers/seed`; run CL-001; assert outbound after `"Looking for nearby providers…"`.
**Non-dev exec:** seed via dev endpoint then run real WA flow.

### MA-002 — Self-exclusion (sender is also provider)  [P0] [both]

**Preconditions:** sender `+X` is a listed plumbing provider AND also creates a plumbing client request (D3 setup). NOTE: same-service dual-role is now blocked at funnel (CL-005). **MA-002 reproduces by listing for `plumbing-emergency` and requesting `plumbing` — different slugs, same phone, same coords.**
**Expected:** matcher excludes `request.ClientPhone` from candidates. Top-N does NOT include sender's own number.
**Dev exec:** seed cross-slug self; run client request; assert exclusion.
**Non-dev exec:** same.
**Linked defect:** D3.

### MA-003 — Distance ranking  [P1] [both]

**Preconditions:** 3 providers at 1 km, 4 km, 8 km.
**Expected:** order in outbound = closest first. (Score = `0.6×proximity + 0.3×recency + 0.1×success`.)
**Dev exec / Non-dev exec:** seed three providers; assert outbound list order.

### MA-004 — Cold-start gating (success term dropped)  [P1] [both]

**Preconditions:** providers all have `< 3` completed `MatchFeedback` rows.
**Expected:** weights re-normalize to `0.67 distance / 0.33 recency`. Verify via log lines from `MatchScorer` (or via inspecting `ProviderStats` table — empty/short list).
**Dev exec / Non-dev exec:** seed; run; inspect logs.

### MA-005 — Recency half-life ~8h  [P2] [both]

**Preconditions:** providers identical except `LastActiveAt` (now-1h, now-8h, now-24h).
**Expected:** `recency_score = exp(-hours/12)` → recent ranks substantially higher.
**Dev exec / Non-dev exec:** seed; assert order matches manual exp computation.

### MA-006 — Providers not notified at match-presentation time  [P1] [both]

**Preconditions:** matched providers presented to client.
**Expected:** `ServiceRequestCreatedHandler` calls `presenter.PresentAsync` for the client only. **No outbound to any provider at this point.** Provider notification happens in `PhoneExchanger.TryExchangeAsync` only when the client sends a PICK command (see CN-001, CN-007).
**Dev exec:** run CL-001 through match presentation; assert outbox contains exactly 1 outbound (to the client listing matches). Assert no outbounds to provider phones.
**Non-dev exec:** same — verify only the client receives the match list.
**Notes:** replaces former "fan-out at match time" design. Provider privacy: they are unaware they were matched until explicitly picked.

### MA-007 — Match presentation includes PICK instructions  [P1] [both]

**Preconditions:** ≥1 match available.
**Expected:** `MatchPresenter` outbound includes PICK command hint, e.g. `"Reply PICK 1 to PICK <N> to share contact, NEXT for more, or NEW for a different service."` Exact phrasing may be AI-rephrased; assert the presence of PICK keyword and index range.
**Dev exec:** run CL-001 through to match presentation; assert outbound contains `PICK` and index numbers.
**Non-dev exec:** same.

### MA-008 — No PICK re-notification on NEXT  [P1] [both]

**Preconditions:** CN-001 or CN-007 ran (at least one provider picked); client sends `"NEXT"`.
**Expected:** new providers presented to client; already-picked providers do NOT receive another outbound. `match.PickedAt` for previously picked matches unchanged.
**Dev exec / Non-dev exec:** standard.

### MA-009 — First exhaustion auto-expand 5→10  [P1] [both]

**Preconditions:** at 5km none after first batch.
**Expected:** `request.AutoExpandedOnce` flips, radius doubles, second presentation prefixed `"Showing matches within 10km (wider range):"`.
**Dev exec:** standard.
**Non-dev exec:** standard.

### MA-010 — Second exhaustion → INCREASE prompt  [P1] [both]

**Preconditions:** auto-expanded once already; second exhaustion at 10km.
**Expected:** `"No more in 10km. Reply INCREASE for 20km, or NEW for different service."`.
**Dev exec / Non-dev exec:** standard.

### MA-011 — INCREASE doubles 10→20→40→80→100  [P1] [both]

**Preconditions:** sequence of `"INCREASE"` replies.
**Expected:** radius doubles each time, capped at 100. Outbound prefix `"Showing matches within <R>km (wider range):"` for each.
**Dev exec:** loop POST `"increase"`; assert prefix and `service_requests.CurrentRadiusKm` row value.
**Non-dev exec:** same.
**Linked defect:** D1 (`"increase"` previously misclassified as ServiceRequest with active Open request → silently dropped). Regression test.

### MA-012 — INCREASE at 100km cap  [P1] [both]

**Preconditions:** `CurrentRadiusKm=100`.
**Expected:** `"No providers found in 100km. Try a different service or check back later."`. No further radius change.
**Dev exec / Non-dev exec:** standard.

### MA-013 — Initial empty matches  [P1] [both]

**Preconditions:** no providers within 5km for service.
**Expected:** outbound `"No providers found nearby. Reply INCREASE to widen the search or NEW to change the service."`.
**Dev exec / Non-dev exec:** standard.

### MA-014 — AI top-match summary refusal fallback  [P1] [both]

**Preconditions:** `IConversationAi` returns text containing safety-refusal phrase (`"I'm sorry, but I can't"` etc.).
**Expected:** `AiReplyHelper.TryGenerateOrFallbackAsync` detects refusal, falls back to deterministic format `"Top matches for <slug>: 1. ... 2. ...\nReply PICK 1 to PICK N to share contact..."`. **No** refusal text reaches user.
**Dev exec:** stub `IConversationAi` (test-only DI swap) OR force via prompt manipulation; assert outbound is the fallback shape.
**Non-dev exec:** N/A — cannot reliably force refusal in real Ollama. Verify in dev only.
**Linked defect:** D4.

### MA-015 — Greeting from non-listed sender  [P1] [both]

**Preconditions:** sender is NOT listed and has no active draft/request. AI classifies `"hello"` as `Greeting`.
**Expected:** cold reply `"Hi! I connect people with local service providers. Reply 'I need …' to hire someone, or 'I offer …' to list a service."`.
**Linked defect:** D1 (AI may misclassify `"hello"` as `ProviderRegistration`). Regression — pinned to PASS only when fixed.

---

## § 5. Connection / contact exchange

### CN-001 — PICK 1 with share=true (both sides)  [P0] [both]

**Preconditions:** matches presented; picked provider has `ShareContact=true` AND `ServiceRequest.SharePhoneNumber=true`.
**Expected:** client sends `"PICK 1"` → outbound `"Provider for <slug>: <provider-phone>. Reach out directly."`. Provider receives `"Client wants <slug> (<client-phone>). Expect a message."`. `match.ContactShared=true`, `match.PickedAt` set. `ContactExchanged` event published.
**Dev exec / Non-dev exec:** standard.

### CN-002 — PICK out of bounds  [P2] [both]

**Preconditions:** 2 matches presented.
**Expected:** `"PICK 9"` → no outbound (silent). `PickProviderResolver.Resolve` returns null.
**Dev exec / Non-dev exec:** standard.

### CN-003 — PICK with share=false → chat link  [P0] [both]

**Preconditions:** picked provider has `ShareContact=false`.
**Expected:** `PhoneExchanger.TryExchangeAsync` publishes `ChatRoutingRequested`. **No phone number** sent to either side. `ChatRoutingRequestedHandler` creates `chat_sessions` row + 2 `chat_participants` with random tokens.
- Client outbound: `"The other party prefers a private chat. Open: <ClientUrl>"`
- Provider outbound: `"A client wants to chat with you. Open: <ProviderUrl>"`
- `match.ChatId` set to new chat id.
- Idempotent: re-firing `ChatRoutingRequested` for an already-routed match is a no-op (`if (match.ChatId is not null) return`).
**Dev exec:** seed share=false provider; run pick; assert `chat_sessions` row, both outbounds with link.
**Non-dev exec:** same.

### CN-004 — Free-text "yes" share with single match  [P1] [both]

**Preconditions:** exactly 1 match presented.
**Expected:** client sends `"yes"` (intent=Confirmation) → `ShareTopOrAskAsync` invokes `PhoneExchanger.TryExchangeAsync` directly on match[0].
**Dev exec / Non-dev exec:** standard.

### CN-005 — Free-text "yes" with multiple matches  [P1] [both]

**Preconditions:** N matches presented.
**Expected:** `"yes"` → `"Which match? Reply 1, 2, or N."`. No phone shared.
**Dev exec / Non-dev exec:** standard.

### CN-006 — ContactExchanged event raised once  [P1] [dev]

**Preconditions:** as CN-001.
**Expected:** `ContactExchanged` published exactly once on first share. Subsequent PICK on same match does NOT republish.
**Dev exec:** Wolverine local message log (or temporary handler counting events).
**Non-dev exec:** N/A — internal event, not user-visible.

### CN-007 — PICK ALL selects all presented providers  [P1] [both]

**Preconditions:** ≥2 matches presented; all `ShareContact=true`; `ServiceRequest.SharePhoneNumber=true`.
**Expected:** `"PICK ALL"` → `PhoneExchanger.TryExchangeAsync` called for each match. Each provider receives client phone; client receives each provider phone (one message per provider). All `match.PickedAt` set.
**Dev exec:** seed 2+ providers; run CL-001; send `"PICK ALL"`; assert N outbounds to providers + N outbounds to client.
**Non-dev exec:** same via WA Web.

### CN-008 — PICK 1,3 multi-select  [P1] [both]

**Preconditions:** ≥3 matches presented.
**Expected:** `"PICK 1,3"` → picks matches at positions 1 and 3 only. Position 2 unpicked (`PickedAt=null`). Duplicate index (e.g. `"PICK 1,1"`) results in one pick (deduped).
**Dev exec:** seed 3 providers; run PICK 1,3; assert positions 1 and 3 have `PickedAt` set; position 2 has `PickedAt=null` and received no outbound.
**Non-dev exec:** same.

### CN-009 — Phone fragment pick (last 4 digits)  [P1] [both]

**Preconditions:** match presented; provider phone ends in `1234`; `ShareContact=true`; `ServiceRequest.SharePhoneNumber=true`.
**Expected:** client sends `"1234"` → `PickProviderResolver.MatchByPhoneFragment` returns that provider; exchange proceeds as CN-001.
**Dev exec:** seed provider with known phone; send last-4 fragment; assert match.
**Non-dev exec:** same.

### CN-010 — Re-pick idempotency  [P1] [both]

**Preconditions:** CN-001 completed; `match.ContactShared=true`.
**Expected:** second `"PICK 1"` → client receives reminder (provider phone again); **provider NOT re-notified**; `ContactExchanged` NOT re-published; `match.ContactShared` unchanged.
**Dev exec:** run CN-001; send `"PICK 1"` again; assert only 1 outbound (to client); assert `ContactExchanged` count unchanged.
**Non-dev exec:** same.

### CN-011 — Unpicked providers receive zero messages  [P1] [both]

**Preconditions:** 3 matches presented; client picks only match 1.
**Expected:** matches 2 and 3 have `PickedAt=null`. No outbound messages sent to providers at positions 2 and 3 from the pick flow (or from match presentation — provider notification only occurs at PICK time, not at match-presentation time).
**Dev exec:** seed 3 providers; run CL-001; send `"PICK 1"`; assert outbox contains no messages to providers 2 and 3.
**Non-dev exec:** same.

### CN-012 — Client opted out → chat link regardless of provider consent  [P1] [both]

**Preconditions:** `ServiceRequest.SharePhoneNumber=false`; picked provider has `ShareContact=true`.
**Expected:** `"PICK 1"` → chat routing triggered (same as CN-003 path). No phone exchange. Client and provider both receive chat link.
**Dev exec:** run CL-001 with NO at consent step; seed share=true provider; PICK 1; assert `chat_sessions` row and no phone exchange.
**Non-dev exec:** same.

---

## § 6. Web chat (SignalR + E2E crypto)

### CH-001 — `GET /api/chat/open` with valid token  [P0] [both]

**Preconditions:** chat session created (after CN-003); valid token.
**Expected:** HTTP 200, body `{ chatId, participantId, role, sessionId, status: "Active", expiresAt }`. `chat_access_log` row inserted with IP and UA. `chat_participants.IsActiveSession=true` for new sessionId.
**Dev exec:** `curl /api/chat/open?token=<t>`.
**Non-dev exec:** open browser to `/c/<chatId>/<token>`; verify network tab response.

### CH-002 — `GET /api/chat/open` with invalid token  [P1] [both]

**Expected:** HTTP 404.
**Dev exec / Non-dev exec:** standard.

### CH-003 — SignalR connect → HistoryLoaded  [P0] [both]

**Preconditions:** open returned valid session; participant has prior messages.
**Expected:** WebSocket connect with `?token=<t>&sessionId=<s>` → server invokes `Clients.Caller.SendAsync("HistoryLoaded", [...])` with last 50 messages (ciphertext + nonce + sequence).
**Dev exec:** Node `@microsoft/signalr` client OR browser `useChatHub`. Listen for `HistoryLoaded`.
**Non-dev exec:** open browser → DevTools console → assert hub event.

### CH-004 — Both sides PublishKey → PeerKeyAvailable  [P0] [both]

**Preconditions:** both participants connected.
**Expected:** each calls `PublishKey(<spki-base64>)`. After both keys present, both receive `PeerKeyAvailable { peerParticipantId, peerPublicKeyB64 }`. Per `frontend/src/crypto/chatCrypto.ts`, frontend derives shared secret via P-256 ECDH + HKDF-SHA-256.
**Dev exec:** scripted SignalR client publishing fixture keys.
**Non-dev exec:** open both `/c/...` URLs in two browser tabs; observe `PeerKeyAvailable` in DevTools.

### CH-005 — SendMessage round-trip  [P0] [both]

**Preconditions:** keys exchanged.
**Expected:** caller sends `EncryptedMessageDto { CiphertextB64, NonceB64, Sequence }`. Server stores `chat_messages` row, broadcasts `MessageReceived` to chat group. Both sides see the cipher; both can decrypt locally.
**Dev exec:** scripted client.
**Non-dev exec:** type in MessageInput on tab A; observe MessageList update on tab B.

### CH-006 — Oversized ciphertext rejected  [P1] [dev]

**Preconditions:** keys exchanged.
**Expected:** ciphertext > 5000 bytes → silent return (no row, no broadcast). Connection stays open.
**Dev exec:** scripted client with 6000-byte ciphertext.
**Non-dev exec:** N/A — frontend won't generate that.

### CH-007 — Replayed sequence rejected  [P1] [dev]

**Preconditions:** keys exchanged; `participant.NextSequence=5`.
**Expected:** SendMessage with `Sequence=4` → log `"Replayed or out-of-order sequence rejected"`. No row written. No broadcast.
**Dev exec:** scripted client.
**Non-dev exec:** N/A.

### CH-008 — SendMessage in ended session  [P1] [both]

**Preconditions:** `chat_sessions.Status=Ended`.
**Expected:** server sends `SessionEnded` to caller; no broadcast.
**Dev exec:** end via `EndChat`, then attempt SendMessage.
**Non-dev exec:** same in 2 tabs.

### CH-009 — EndChat manual end  [P0] [both]

**Preconditions:** active chat, both connected.
**Expected:** caller invokes `EndChat()` → `chat_sessions.Status=Ended`. Group receives `ChatEnded { reason: "user", endedBy: <Role> }`. Frontend ChatRoom shows "Chat ended by the <role>".
**Dev exec:** scripted client.
**Non-dev exec:** click "End chat" in UI; confirm dialog; both tabs go to ended state.

### CH-010 — Re-open chat URL revokes prior session  [P0] [both]

**Preconditions:** participant connected via session A.
**Expected:** participant opens same `/c/.../<token>` URL again → `RotateSession()` issues new sessionId. Old hub connection receives `SessionRevoked` and is aborted by `Context.Abort()`. Frontend RevokedToast shown on old tab.
**Dev exec:** open chat in Node SignalR client A; call `/api/chat/open` again; reconnect as session B. Verify A receives `SessionRevoked`.
**Non-dev exec:** open in two tabs of same browser using same link; second open revokes first.

### CH-011 — Idle reminder at 20 min  [P1] [both]

**Preconditions:** chat active, no activity for 20 min (or scheduled event fired).
**Expected:** `IdleReminderHandler` → group receives `IdleReminder { message: "Are you still available? Reply to continue." }`. Status remains Active.
**Dev exec:** publish `IdleReminderCheck { ChatId, LastActivityAt }` directly via prereq endpoint.
**Non-dev exec:** same.

### CH-012 — Idle reminder skipped on fresher activity  [P1] [both]

**Preconditions:** scheduled `IdleReminderCheck` fires but `session.LastActivityAt > evt.LastActivityAt`.
**Expected:** handler logs `"Skipping idle reminder ... fresher activity"`. No `IdleReminder` event sent.
**Dev exec:** send a message right before publishing event with stale `LastActivityAt`.
**Non-dev exec:** same.

### CH-013 — Idle end at 30 min  [P1] [both]

**Preconditions:** `IdleEndCheck` published, no fresher activity.
**Expected:** `chat_sessions.Status=Ended`, group receives `ChatEnded { reason: "idle" }`. Frontend shows "Chat ended due to inactivity."
**Dev exec / Non-dev exec:** standard via direct event.

### CH-014 — Hard expire at 24 h  [P1] [both]

**Preconditions:** chat reaches `ExpiresAt`.
**Expected:** `HardExpireHandler` flips status to `Expired`. Subsequent `/api/chat/open` returns `status: "Expired"`. Frontend ChatRoom shows "This chat has expired."
**Dev exec:** publish hard-expire event; or psql `update chat_sessions set "ExpiresAt"=now() - interval '1 minute'` and re-open.
**Non-dev exec:** same.

### CH-015 — Open expired chat URL  [P1] [both]

**Preconditions:** session expired or ended.
**Expected:** `/api/chat/open` returns `status: "Expired"` or `"Ended"`. Frontend ChatRoom renders centered message; no SignalR connect attempted.
**Dev exec / Non-dev exec:** standard.

### CH-016 — Connect with stale sessionId after revoke  [P1] [both]

**Preconditions:** CH-010 ran; old sessionId no longer current.
**Expected:** OnConnectedAsync sees `!participant.IsCurrentSession(stale)` → sends `SessionRevoked` and aborts.
**Dev exec:** scripted client with old sessionId.
**Non-dev exec:** browser reload after revoke would call `/api/chat/open` and rotate again; reproducing requires manually crafting URL with stale id.

### CH-017 — Frontend end-chat confirm dialog  [P2] [real]

**Preconditions:** chat active in browser.
**Expected:** click "End chat" → modal appears with title "End this chat?" and red "End chat" button. Cancel keeps dialog closed; Esc closes; clicking backdrop closes; clicking confirm calls `endChat()` and disables both buttons during ending.
**Dev exec:** N/A — pure frontend behavior.
**Non-dev exec:** chrome-devtools MCP `click` on End → assert dialog snapshot; click Cancel → dialog gone; reopen → click confirm → ChatEnded path.

### CH-018 — WaitingForPeer until peer key arrives  [P2] [real]

**Preconditions:** only one participant connected; the other has not yet published key.
**Expected:** MessageInput is `disabled=true` and `WaitingForPeer` component visible. After peer publishes key (`PeerKeyAvailable` fires), UI unlocks.
**Dev exec:** N/A.
**Non-dev exec:** open one tab; assert disabled state via snapshot; open second tab; assert state changes.

### CH-019 — Access log row per open  [P1] [both]

**Preconditions:** valid token.
**Expected:** each `/api/chat/open` call inserts `chat_access_log { ChatId, ParticipantId, IpAddress, DeviceInfo, OpenedAt }`.
**Dev exec / Non-dev exec:** count rows after N opens.

### CH-020 — Frontend end states render correctly  [P2] [real]

**Preconditions:** chat in {ended-user, ended-idle, expired, revoked}.
**Expected:** centered message strings — `"Chat ended by the <role>."`, `"Chat ended due to inactivity."`, `"Chat expired."`, RevokedToast.
**Non-dev exec:** force each state via dev event injection; reload browser; assert.

---

## § 7. Feedback

### FB-001 — Step1 prompt fires at +4h after contact share  [P0] [both]

**Preconditions:** `ContactExchanged` event published (CN-001).
**Expected:** `ContactExchangedHandler` schedules `Step1FeedbackCheck` at +4h. When fired, `Step1FeedbackHandler` adds `MatchFeedback { MatchId, Step=DidYouFind }` row. Outbound is **AI-rephrased** from `Purpose: "feedback-step-1-did-you-find"` with instruction `"Ask if the client found a service provider. Mention they can reply YES or NO."` — assert the outbound contains an interrogative + tokens YES/NO/yes/no, NOT exact text. If `AiReplyHelper.TryGenerateAsync` returns null, **no outbound sent** (XC-004 path).
**Dev exec:** publish `Step1FeedbackCheck { matchId }` directly.
**Non-dev exec:** same.

### FB-002 — Step1 fires on chat ended (manual or idle)  [P1] [both]

**Preconditions:** chat session transitions to Ended.
**Expected:** Step1 prompt sent at chat-end (subject to handler — verify event subscription). Suppression: max one per match.
**Dev exec / Non-dev exec:** standard.

### FB-003 — Step1 fires at +23h before chat hard-expire  [P1] [both]

**Preconditions:** chat at 23h since creation; no Step1 yet.
**Expected:** Step1 prompt sent (still inside Meta 24h window). `analysis.md §17`.
**Dev exec / Non-dev exec:** standard.

### FB-004 — Step1 YES → Step2 +20h  [P1] [both]

**Preconditions:** Step1 pending.
**Expected:** client replies `"yes"` → `MatchFeedback.Answer=Yes, RepliedAt=now`. `Step2FeedbackCheck` scheduled at +20h.
**Dev exec / Non-dev exec:** standard.

### FB-005 — Step1 NO → no Step2  [P1] [both]

**Preconditions:** Step1 pending.
**Expected:** `"no"` → `Answer=No`. **No** Step2 scheduled.
**Dev exec / Non-dev exec:** standard.

### FB-006 — Step1 unrelated reply  [P2] [both]

**Preconditions:** Step1 pending.
**Expected:** unrecognised text routes through `LazyIntent.GetAsync`; if AI returns `Confirmation` / `Rejection`, treated as YES/NO. Otherwise no answer recorded.
**Dev exec / Non-dev exec:** test with `"maybe"`, `"thanks"`, etc.

### FB-007 — Step2 IN_PROGRESS → reschedule +48h  [P1] [both]

**Preconditions:** Step2 pending.
**Expected:** `"in progress"` reply → `Step2FeedbackCheck` scheduled +48h. No `ProviderStats` change yet.
**Dev exec / Non-dev exec:** standard.

### FB-008 — Step2 YES → ProviderStats success+1  [P1] [both]

**Preconditions:** Step2 pending; `ProviderStats` may or may not exist.
**Expected:** `"yes"` → `ProviderStats.RecordOutcome(success: true)`. `CompletedJobs` increments; `SuccessCount` increments.
**Dev exec:** publish then reply; assert `provider_stats` row delta.
**Non-dev exec:** same.

### FB-009 — Step2 NO → ProviderStats failure+1  [P1] [both]

**Preconditions:** as FB-008.
**Expected:** `"no"` → `RecordOutcome(success: false)`. `CompletedJobs++`, `SuccessCount` unchanged.
**Dev exec / Non-dev exec:** standard.

### FB-010 — Step1 no reply within 48h → skipped  [P2] [both]

**Preconditions:** Step1 sent 48h+ ago, no reply.
**Expected:** `MatchFeedback.Status=Skipped`. No further prompts. (Implementation may be a separate cleanup job — verify.)
**Dev exec:** psql backdate; trigger cleanup handler.
**Non-dev exec:** same.

### FB-011 — Pending feedback routing precedence  [P1] [both]

**Preconditions:** sender has pending `MatchFeedback`. Sender also has no active draft.
**Expected:** any inbound text routes to `FeedbackResponseService.HandleAsync` BEFORE intent detection. Verified via log `"Route → FeedbackResponseService (pending feedback)"`.
**Dev exec / Non-dev exec:** standard.

### FB-012 — Step3 (4h after match shown, no chat created, share=true)  [P2] [both]

**Preconditions:** match presented and contact shared but no chat created (share=true direct path).
**Expected:** Step1 fires at +4h after match presentation (via `ContactExchangedHandler` schedule). Same as FB-001 but pinned to share=true path explicitly.
**Dev exec / Non-dev exec:** standard.

---

## § 8. Cross-cutting

### XC-001 — Rate limit L1 burst (3 / 5 s)  [P1] [both]

**Preconditions:** sender has not been rate-limited recently.
**Expected:** 4th message within 5 s → ignored by orchestrator. Single outbound `"Slow down — try again in <n>s"` (only first hit per cooldown). Subsequent hits silent.
**Dev exec:** burst 5 inbounds in 1 s.
**Non-dev exec:** rapid-tap WA Web composer.

### XC-002 — Rate limit L2 spam (30 / hour)  [P1] [both]

**Preconditions:** none.
**Expected:** 31st message in an hour → cool-down outbound (only first hit), then silent.
**Dev exec:** loop 31 inbounds.
**Non-dev exec:** impractical for full 30 — verify L1 + L2 logic separately, not together.

### XC-003 — Rate limit L3 abuse (10 availability / day, 20 requests / day)  [P1] [dev]

**Preconditions:** none.
**Expected:** 11th availability registration attempt or 21st request creation in 24 h → `"Daily limit reached. Try tomorrow."`. Once.
**Dev exec:** scripted loop.
**Non-dev exec:** N/A scale.

### XC-004 — AI empty reply → drop  [P1] [dev]

**Preconditions:** stub `IConversationAi.ReplyAsync` to return empty string.
**Expected:** per `memory/project_ai_provider.md` — `AiReplyHelper` drops the message; **no** outbound sent. Inbound is logged but no reply.
**Dev exec:** test-only DI swap or env flag forcing empty.
**Non-dev exec:** N/A.

### XC-005 — AI HTTP failure → drop  [P1] [dev]

**Preconditions:** Ollama down or returns 500.
**Expected:** `OllamaConversationAi` throws; `AiReplyHelper` catches and drops. **No** outbound, no fallback hallucination.
**Dev exec:** stop Ollama mid-conversation.
**Non-dev exec:** N/A.

### XC-006 — Geocoder API key missing  [P1] [dev]

**Preconditions:** `GoogleGeocoding:ApiKey=""`.
**Expected:** depending on chosen behavior (D2 fix decision):
- (a) fail loudly: geocode returns null → "Couldn't find that address. Please send your GPS pin." outbound, OR
- (b) silent SF stub: lat=37.7749, lng=-122.4194 used (current behavior — flag in run report).
**Dev exec:** clear key, run PR-007.
**Non-dev exec:** same.
**Linked defect:** D2.

### XC-007 — Geocode cache hit  [P2] [dev]

**Preconditions:** prior call cached `("123 Main St", lat, lng, formatted)` in `geocode_cache`.
**Expected:** second call with same input bypasses HTTP; result identical.
**Dev exec:** wire via test seam or run twice and inspect log absence of HTTP call.
**Non-dev exec:** N/A.

### XC-008 — Language pass-through  [P1] [both]

**Preconditions:** none.
**Expected:** French inbound `"Je cherche un plombier"` → AI responds in French. Slug normalized to canonical English (`plumbing`).
**Dev exec:** `POST inbound { text: "Je cherche un plombier" }`; assert outbound French via regex on `confirmer`/`oui`/etc.
**Non-dev exec:** type French line in WA Web; assert reply language.

### XC-009 — PromptSafety facts shape  [P2] [dev]

**Preconditions:** facts dictionary contains keys with control characters or unexpected types.
**Expected:** `PromptSafety` sanitizes per its rules (covered in `MatchPresenterFactsShapeTests` and `PromptSafetyTests`). End-to-end: `MatchPresenter.PresentAsync` does not crash on weird facts; falls back if AI rejects.
**Dev exec:** unit-tested; E2E only verifies presenter doesn't 500.
**Non-dev exec:** N/A.

### XC-010 — Phone E.164 normalization  [P1] [dev]

**Preconditions:** webhook payload has phone without leading `+` (Meta sometimes sends `12035390050`).
**Expected:** `WebhookParser` / `PhoneNumber.TryParse` normalizes to E.164 with `+` prefix. Stored consistently.
**Dev exec:** craft webhook body with bare-digit phone; assert DB `Phone` field begins with `+`.
**Non-dev exec:** N/A (Meta normalizes in real traffic).

---

## Defect cross-reference (D1–D6 from initial run report)

| Defect | Pinned scenario(s) |
|---|---|
| D1 — AI classifier biases toward action intents | MA-011 (`"increase"` regression), MA-015 (`"hello"` cold reply) |
| D2 — Geocoder always returns SF when key absent | PR-007, XC-006 |
| D3 — Match self-inclusion when sender is also a provider | MA-002 |
| D4 — AI top-match summary refusal text | MA-014 |
| D5 — `cancel` literal maps to Rejection | PR-015 |
| D6 — `/readyz` 2-second probe fails on cold CPU | WHK-009 (Notes) |

A run that flips any of these to PASS proves the defect is fixed. A run that fails one is a regression.

---

## Per-run report template

Copy to `E2E_TEST_REPORT_<YYYYMMDD>.md`:

```markdown
# Hook E2E Test Report — YYYY-MM-DD

## Setup
- Mode: dev | real | both
- Backend SHA: <sha>
- Tester: <name>
- Time-flow exec: /dev/schedule/fire | psql workaround
- Notes: <ngrok URL, model warm, etc.>

## Results
| ID | Result | Notes |
|---|---|---|
| WHK-001 | PASS | |
| ... | | |

## Defects observed
- D<N> — observed at scenario X, severity, repro steps.

## Skipped
- ID — reason (real-manual not run, env issue).
```
