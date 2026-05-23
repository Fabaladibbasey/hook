# Deploy runbook

## Pre-launch deploy (current posture)

Until the first real user signs up, the production DB can be dropped and
recreated freely. This is what we do for any change that renames a
Wolverine message type or alters envelope shape in a non-backward-compat way.

Sequence:
1. Stop the production app.
2. `dropdb hook && createdb hook`
3. Re-run migrations: `dotnet ef database update --project src/Hook.csproj`
4. Restart the app — `RootSectorSeeder` repopulates the 16 root sectors at boot.

Wolverine recreates its own `wolverine.*` schema on startup, so no manual
cleanup is needed there.

## Post-launch deploy (future posture — when we have real users)

Type-rename PRs MUST add `[MessageIdentity("OldTypeName", Version = N)]` aliases.
Wolverine keys envelopes by **short type name**, not FQN — see
`backend/src/Features/Feedback/FeedbackEvents.cs:10` for the live usage
(`[MessageIdentity("Step2FeedbackCheck", Version = 1)]`).

Otherwise document a drain step:
1. Pause webhooks (stop accepting new inbound messages).
2. Wait for the durable queues to drain:
   - `SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE status IN ('Incoming','Scheduled')` = 0
   - `SELECT COUNT(*) FROM wolverine.wolverine_outgoing_envelopes` = 0
     (the outbox poller normally keeps this near zero; if it climbs, stop publishers first).
3. Deploy.
4. Resume webhooks.
