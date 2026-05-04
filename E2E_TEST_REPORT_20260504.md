# Hook E2E Test Report — 2026-05-04

## Setup
- Sender: real WhatsApp account `+2203539005` (logged into web.whatsapp.com via chrome-devtools MCP)
- Bot: Meta test number `+1 (555) 633-2510`
- Webhook: `https://evident-bright-beagle.ngrok-free.app/webhooks/whatsapp` → `:5212` backend
- AI: Ollama `qwen2.5:3b` @ `:11434`
- DB: Postgres `hook` @ `:5432`
- 13 scenarios attempted; 158 inbound/outbound/route-decision log entries observed.

## Results matrix

| # | Scenario | Result | Detail |
|---|----------|--------|--------|
| 1 | Cold-reply greeting (`hello`) | **FAIL** | AI mis-classifies "hello" as ProviderRegistration with conf ≥ 0.6, bypassing ambiguity disambiguation. Bot replies "I detected: auto-repair, cleaning. Reply YES…" instead of cold-reply greeting. |
| 2 | Cold-reply out-of-scope (`what time is it`) | **FAIL** | AI returns ServiceRequest with extracted slug "time" → "Do you need time? Reply YES or NO." Out-of-scope path unreachable. |
| 3 | Provider registration full happy path | **PASS** | 5-step funnel (offer → yes → address → yes → yes) lands `provider_availabilities` row [plumbing], `ShareContact=true`, 24h TTL. Bot final: "You are listed for 24h." |
| 4 | Provider edit branch | **PASS** | "edit" → "Send the corrected list of services in one message." Then "carpentry and painting" replaced `DraftServices` `["plumbing"]` → `["carpentry","painting"]` and re-prompted ConfirmServices. |
| 5 | Provider share=false | **PASS** | Reply "no" at AwaitingConsent → `ShareContact=false`, listed for 24h. |
| 6 | Client request with location pin | **SKIPPED** | chrome-devtools MCP can't programmatically drive WA Web's Attach → Location picker (native UI + geolocation prompt). Geocode-text path covered in S7. Backend's location-kind handler verified by an earlier `kind=Location` inbound at 00:18:42. |
| 7 | Client request with text address | **PARTIAL** | service_requests row created, fan-out broadcast `Client wants plumbing (+CLIENT)` delivered. **Defects:** (A) match selected client's own phone (no self-exclusion when sender is also a listed provider at same coords). (B) AI top-match summary returned safety-refusal text "I'm sorry, but I can't assist with that." |
| 8 | Client PICK on share=true | **PASS** | "PICK 1" → "Provider for plumbing: +PHONE. Reach out directly." `ContactExchanged` event raised, Step1 feedback scheduled at +4h. (Phone shared was sender's own, due to S7 self-match defect.) |
| 9 | Pagination next/increase | **PARTIAL** | "next" PASS — "No more in 10km. Reply INCREASE…", `Iteration exhausted` log. "increase" **FAIL** — AI returns `intent=ServiceRequest, conf=0.85` for "increase"; router falls through to default `No route for inbound` and **silently drops** the message. |
| 10 | Wrong-step recovery | **PASS** | "blue cheese" at ConfirmService step → silent reprompt "Reply YES or NO."; step unchanged. |
| 11 | Listed-provider heartbeat | **PASS** | "thanks" from listed provider extends `ExpiresAt` (01:25:51 → 01:26:33), zero outbound reply. Heartbeat silent as designed. |
| 12 | Provider also creates client request | **PASS** | Listed provider sends "I need an electrician" → routed to `ClientRequestOrchestrator (new request)` with slug `electrical`. Dual-role path works. |
| 13 | E2E chat handoff (share=false PICK) | **SKIPPED** | Only candidate share=false plumbing provider (+14155550121 at SF) had `ExpiresAt = 2026-05-03 < now` → inactive, correctly excluded by matcher. Authorization to extend the shared seed row's TTL was denied. Code path verified by inspection: `PhoneExchanger.TryExchangeAsync` → emits `ChatRoutingRequested` → both sides receive `/c/<chatId>/<token>`. |

**Tally:** 7 PASS · 2 FAIL · 2 PARTIAL · 2 SKIPPED.

## Defects (severity-ordered)

### D1. AI classifier biased toward action intents (Scenarios 1, 2, 9)
- qwen2.5:3b w/ full `AiPrompts.IntentSystem` returns ProviderRegistration for "hello" and ServiceRequest for "what time is it" / "increase" — at confidence ≥ 0.6, so router's ambiguity disambiguation also doesn't fire.
- Greeting and Unknown branches in `InboundRouterHandler` are therefore unreachable for plain greetings and chitchat → cold-reply paths (`SendColdReplyAsync` w/ `greeting-reply` / `out-of-scope` purpose) never invoked from real WA.
- "increase" misclassified as ServiceRequest → with active Open request, silently dropped at `default: No route` (line 156-158).
- Isolated probe with a *shorter* system prompt: `qwen2.5:3b` correctly returns Greeting for "hello" — confirms it's the prompt's own provider-heavy examples that bias the model.
- **Suggested fixes:** add `QuickIntent.DetectIntentHint` regex for greeting tokens (`hello/hi/hey/morning/...`) and pagination tokens (`next/more/increase/wider`); or move to a larger classifier; or rewrite IntentSystem prompt to lead with Greeting/Unknown examples.

### D2. Geocoder always returns San Francisco (Scenarios 3, 7, 13)
- `GoogleGeocoding:ApiKey=""` (appsettings.json). All text addresses — including "Independence Drive, Banjul, Gambia" — resolve to `(37.7749, -122.4194)` (the SF stub fallback).
- Cascade effects: dev runtime providers around Banjul become unreachable to a sender at SF coords (and vice versa); E2E chat handoff and proper match scoring depend on real geo lookup.
- **Suggested fix:** populate `GoogleGeocoding:ApiKey` in dev environment, OR fail loudly when key is absent rather than silently SF-stubbing.

### D3. Match self-inclusion when sender is also a listed provider (Scenario 7)
- Sender +2203539005 was a listed plumbing provider at SF. When sender submitted a plumbing client request at SF, the matcher returned **the sender's own phone** as the top match. PICK then "shared" sender's own contact back to themselves.
- **Suggested fix:** matcher should exclude `request.ClientPhone` from candidate set.

### D4. AI top-match summary returns refusal text (Scenario 7)
- After "Looking for nearby providers…", AI was prompted to summarize matches but replied: "I'm sorry, but I can't assist with that." — qwen2.5:3b safety-trained refusal was sent verbatim to the user as an outbound message.
- **Suggested fix:** either tune the summary prompt to defeat the safety-aligned refusal, treat the refusal output as a drop (similar to `AiReplyHelper` empty-reply handling), or add a regex-based filter for safety-refusal phrases before sending.

### D5. `cancel` literal maps to Rejection, not Cancel (UX issue)
- `QuickIntent.Detect`: `"cancel"` → `IntentKind.Rejection`. To actually abort a draft, user must type `end` / `bye` / `quit` / `goodbye` / `exit` / `leave` / `done`.
- During testing, "cancel" at ConfirmServices step produced silent reprompt instead of aborting. This is a likely UX trap for real users.
- **Suggested fix:** add "cancel"/"stop" to the Cancel token set, OR document the abort word more clearly to users.

### D6. `/readyz` 2-second probe always returns 503 on local dev hardware
- `AiReadinessProbe.ProbeTimeout = 2s`, but qwen2.5:3b JSON-mode inference takes ~5s warm on local CPU. Probe permanently red on dev.
- Cosmetic — doesn't block message flow (which uses `OllamaOptions.TimeoutSeconds = 120`). But k8s liveness checks will permanently fail in environments without GPU.
- **Suggested fix:** make `ProbeTimeout` configurable, or use a cached non-AI ping (e.g. HTTP HEAD on `/api/tags`) instead of running full intent detection.

## What worked well
- Provider registration funnel (S3, S4, S5) — deterministic, regex-driven, every step resolved as expected.
- Listed-provider heartbeat (S11) — TTL extended without outbound noise.
- Dual-role routing (S12) — listed provider sending a service request correctly hits `ClientRequestOrchestrator`.
- Wrong-step recovery (S10) — silent reprompt as documented.
- Fan-out broadcast (S7) — "Client wants plumbing (+CLIENT). Expect a message." delivered to share-true providers.
- PICK protocol (S8) — `ContactExchanged` event, Step1 feedback scheduling.
- Webhook signature validation, dedup by MessageId, Wolverine event bus all passing under real Meta delivery (multiple retries observed and de-duplicated).

## Test environment notes
- **chrome-devtools MCP profile lock**: requires killing all Chrome processes using the MCP profile dir before each session start; user permission needed for `Stop-Process`.
- **WA Web auto-clear of contenteditable on focus**: `document.execCommand('delete')` does NOT clear the composer in current WA build — observed text-doubling when focus + type_text happens twice without an intervening Enter. Workaround: send Enter quickly after each type_text.
- **Meta delivery latency**: `webhook → orchestrator → outbound` round-trip ~5–12 seconds end-to-end (Meta inbound delay + AI inference + Meta outbound delay).
- **/dev/whatsapp/* dev console** (`Dev:Whatsapp:Enabled=false`) was not used in this run — all inbound was real WA via Meta Cloud API.

## Code-level confidence on untested paths
- **S13 chat handoff (share=false PICK):** path inspected in `PhoneExchanger`, `IMatchRepository`, hub events. Logic appears correct, but no live verification.
- **S6 location-pin:** `kind=Location` inbound parsed by `WebhookParser`; orchestrator's `CollectLocationAsync` handles `Latitude`/`Longitude` from message payload. Earlier 00:18:42 log line shows `kind=Location` inbound was processed. Not re-verified in this run.
