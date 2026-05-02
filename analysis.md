# Hook — Story Analysis & Design Decisions

Output of `/ccw:analyze-story` against `prd.md`. Reference document for `/plan-task` and downstream development.

---

## Story Summary

WhatsApp-first connector. Clients describe a need in natural language. AI extracts intent, service, and location. System matches nearby providers by distance + recency + success score, returning top 2–3. Privacy-aware: share phone numbers only on mutual consent; otherwise both sides use a signed SignalR chat link with idle/expiry lifecycle and single-active-session enforcement. Feedback loop feeds success rate back into ranking.

---

## 1. WhatsApp Integration

- **Provider:** Meta WhatsApp Cloud API (official, direct).
- Webhook ingress to `.NET` service.
- Outbound subject to **Meta 24h customer service window** (see §22).

---

## 2. Backend Stack

- **.NET 10** for API + matching + AI orchestration.
- **.NET SignalR** for real-time chat.
- **Postgres** with **EF Core** ORM.
- **Architecture:** vertical slices + Domain-Driven Design.
- **Testing:** xUnit + Shouldly assertions.
- **CI/CD:** GitHub Actions — code quality (lint, tests, deployment).

---

## 3. Geo Engine

- **PostGIS** extension on Postgres.
- `geography` type, `ST_DWithin` / `ST_Distance` for radius and nearest-N.
- GIST index for performance.
- Self-host PostGIS supported on chosen hosting target.

---

## 4. AI Model

- **Pluggable abstraction** with **Gemini** as default.
- Easy swap to Claude/OpenAI/other later via interface.
- AI handles: intent detection, service extraction, entity extraction, clarification, multilingual replies, free-form conversational messaging.
- Deterministic system handles: matching, distance, ranking, privacy rules, chat lifecycle.

---

## 5. Hosting

- **Primary:** VPS (Hetzner / DO) with Docker Compose.
- **Fallback consideration:** Supabase Postgres + container host elsewhere.
- PostGIS available on both paths.

---

## 6. Service Taxonomy

- **Free-form** — AI normalizes user input to a slug.
- No fixed canonical list; taxonomy emerges from real usage.
- Dedup strategy required to prevent the same service registering as different slugs (see §7).

---

## 7. Service Slug Dedup

**Hybrid: trigram pre-filter + AI judge.**

- Postgres `pg_trgm` extension for similarity scoring on existing slugs.
- Thresholds:
  - `similarity ≥ 0.85` → auto-merge (reuse existing slug)
  - `0.50 ≤ similarity < 0.85` → AI judges with top-3 candidates ("match or new?")
  - `< 0.50` → treat as new slug
- Tunable thresholds.

---

## 8. Search Radius

- **Default 5 km.**
- "Increase range" doubles each step: 5 → 10 → 20 → 40 → 80 → 100 (cap).
- 100 km hard cap.
- Service-type-aware radius deferred to v2.

---

## 9. Ranking Formula

```
score = (0.6 × proximity) + (0.3 × recency) + (0.1 × success_rate)
```

- **Cold-start gating:** success term applied only if provider has **≥ 3 completed feedback jobs**.
- Below threshold, weights re-normalize to **0.67 distance / 0.33 activity** (drop success entirely for that provider).
- **Recency formula:** `recency_score = exp(-hours_since_active / 12)` — half-life ~8 hours.
- Weights configurable later.

---

## 10. Provider Availability Refresh

`expires_at = 24h`. Refresh triggers (combo of two patterns):

1. **Auto-prompt at 22h:** "Still available?" — YES extends 24h, no reply expires at 24h.
2. **Any inbound message** from provider extends `expires_at` by 24h (treat any reply as a heartbeat).

---

## 11. Mixed-Consent UX

When one side disabled `share_contact` → both must use chat link.

- **Inform the consenting side** explicitly: "Other party prefers chat — here's the link."
- Reveals the other party's choice to the consenting one. Honest about the constraint.
- Other party gets the link without explanation (their choice already made).

---

## 12. Chat Token Scheme

**Opaque random token + DB lookup.**

- Token = `RandomNumberGenerator.GetBytes(32)` → Base64Url (~43 chars).
- Stored on `chat_participant.token`, indexed.
- Single-active-session enforcement: PRD requires DB lookup on every connection regardless, so JWT statelessness gains no real performance edge.
- Revocation = update `is_active_session` flag on the prior token row.
- **Migration path:** can switch to JWT + session-version pattern if real perf bottleneck emerges later.

---

## 13. Idle / Expiry Config

- `appsettings.json` configurable values.
- Defaults from PRD: 20 min idle reminder, 30 min total → ended, 24h hard expiry.
- Restart to apply. No DB-backed live tuning in v1.

---

## 14. Revoked-Session UX

When a participant opens their chat link a second time → prior session revoked.

- **SignalR `SessionRevoked` event** pushed to old connection.
- Client shows toast: "Session opened on another device — disconnecting."
- ~3 second display, then forced disconnect + page replace with "Session ended (opened elsewhere)".
- Prevents PRD-flagged third-party listening / link sharing.

---

## 15. Access Log Retention

`chat_access_log` lifecycle = chat lifecycle.

- Logs deleted (cascade) when their parent chat row ends/expires and is purged.
- No long-term audit retention v1.

---

## 16. Rate Limits

**Three-layer design, per phone number:**

| Layer | Limit | Catches |
|---|---|---|
| Burst | 3 messages / 5 seconds | Double-tap, fast-finger, basic flood |
| Spam | 30 messages / hour | Sustained sub-burst bot |
| Abuse | 10 availability / day, 20 requests / day | Long-game abuse |

**Implementation:**

- **Phase 1 (single instance):** .NET built-in `System.Threading.RateLimiting.PartitionedRateLimiter<string, string>`, partition key = phone parsed from webhook body. In-memory store. Direct API (not middleware) — partition key lives in body, not route.
- **Phase 2 (scale-out):** swap to Redis backend. Either `RedisRateLimiting` NuGet or custom Lua token-bucket script via `StackExchange.Redis`.
- **Migration trigger:** adding 2nd app instance behind LB, or restart-safety becomes critical.

**Failure response:**

- L1/L2 hit → ignore message + WhatsApp reply "Slow down — try again in {n}s" (only first hit per cooldown to avoid loop).
- L3 hit → "Daily limit reached. Try tomorrow."

---

## 17. Feedback Trigger Timing

**Signal-driven hybrid, two stages.**

### Step 1 — "Did you find a provider?"

Fired on first of:

- Chat session transitions to `ended` (manual close or 30m idle).
- Chat about to expire — **fire at 23h** (still inside Meta 24h customer-service window).
- Contact-shared, no chat created → fire **4h after match shown**.

### Step 2 — "Was the job completed?"

- Fired **20h after** Step 1 client reply (kept inside the new window opened by Step 1's reply, not 24h).
- If Step 2 answer = `IN PROGRESS` → re-ask 48h later, then drop.
- If Step 1 = `NO` → skip Step 2.

### Suppression

- Max one Step 1 prompt per match.
- If client doesn't reply to Step 1 within 48h → mark feedback as `skipped`, do not pester.

### Implementation

- Background jobs via **Hangfire** or **Quartz.NET** scheduled at trigger event with `match_id`, `stage`, `fire_at`. Cancellable if superseded.

---

## 18. Language Support

- **AI auto-detects** inbound language → replies in same language (LLM-native, zero extra infra).
- **System templates** (rate-limit warning, hard errors) stay English in v1.
- **Service slugs** stored canonical English (e.g. `plumbing`, not `plomberie`) — AI normalizes from any language to English slug. Enables cross-language matching.
- Migrate to formal i18n only if metrics show >20% traffic in a single non-English language.

---

## 19. Multi-Service Provider Registration

**Multi-extract + granular edit + cap.**

- AI extracts all services from a single message ("I fix doors and repair laptops" → carpentry, computer repair).
- Confirms list: "I detected: Carpentry, Computer Repair. YES / EDIT".
- `EDIT` branches into granular flow: "Remove which? Or ADD: send new services". Loops until YES.
- **Hard cap: 5 services per provider.** If extracted count > 5 → "Max 5 services. Which 5?".
- Dedup: AI extracts duplicate slugs → dedup before showing confirmation.

---

## 20. NEXT / MORE Exhaustion

**Hybrid auto-expand + explicit prompt.**

- **1st exhaustion at current radius:** auto-expand once (5 → 10 km), return next batch with annotation: "Showing matches within 10km (wider range): ...".
- **2nd exhaustion:** explicit prompt — "No more in 10km. Reply INCREASE for 20km, or NEW for different service."
- **3rd+ exhaustion:** same explicit-prompt pattern (20 → 40 → 80 → 100 cap).
- **Hard exhaustion at 100km:** "No providers found in 100km. Try a different service or check back later."
- Track `client_request.shown_provider_ids` and `current_radius_km`.

---

## 21. Geocoding

**Prefer GPS pin, fallback to text → geocode + confirm.**

- Initial prompt requests GPS attachment.
- If user types address → geocode via **Google Geocoding API** ($200/month free credit ≈ 40k calls/month).
- Always confirm result: "Found: '{formatted_address}'. Confirm? YES / SEND PIN INSTEAD".
- Aligns with PRD principle: "Never silently assume."
- **Cache geocoded results** in `geocode_cache` table (key = normalized lowercase text, TTL = forever).
- **Failure mode:** Google API down/quota hit → "Couldn't find address — please send GPS pin."

Why Google over OSM Nominatim: better emerging-market coverage (West Africa, South Asia, LATAM) where OSM data is patchy; cleaner JSON; generous free tier.

---

## 22. Meta 24h Customer Service Window

**Platform constraint, not a content style choice.** Meta blocks free-form outbound > 24h after user's last inbound. Templates required outside the window.

### Strategy

1. **AI smart messaging is the default for all in-window outbound.** Conversational, multilingual, no templates needed.
2. **Restructure timing to stay inside the window:**
   - Step 1 feedback fires at **23h** before chat expiry, not 24h+.
   - Step 2 feedback fires at **20h** after Step 1 reply, not 24h.
   - Provider new-match notification: only sent to providers active within 24h (PRD's `expires_at = 24h` enforces this naturally — stale providers aren't matched).
3. **One Utility-category template safety net** for rare edge cases:

   ```
   Template: provider_check_in (Utility)
   Body: "Still available for {{1}}? Reply YES to stay listed or pause to take a break."
   ```

   AI fills `{{1}}` with comma-joined services. Utility templates approve fast (24–48h), low cost.

### Implementation discipline

- Each outbound message: check `last_inbound_at` from user. Within 24h → AI free-form. Outside → template with AI-filled variables.
- Telemetry: count outside-window sends. If > 5% of total outbound, restructure further.

---

## Cross-Cutting Notes

- **Privacy / data deletion** (GDPR-style erasure): deferred to v2.
- **Phone validation:** normalize to E.164 at ingress.
- **Service-type-aware radius:** v2.
- **AI prompt-injection safety:** rely on Gemini's built-in safety; revisit if abuse seen.

---

## Status

Story analysis complete. Next step: run `/plan-task` (or `/ccw:plan-task`) to produce the implementation plan.
