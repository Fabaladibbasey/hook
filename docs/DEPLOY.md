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

Type-rename PRs MUST add `[MessageIdentity("OldFqn", Version = N)]` aliases.
Otherwise document a drain step:
1. Pause webhooks (stop accepting new inbound messages).
2. Wait until `wolverine_incoming_envelopes`, `wolverine_outgoing_envelopes`,
   and `wolverine_scheduled` are empty.
3. Deploy.
4. Resume webhooks.
