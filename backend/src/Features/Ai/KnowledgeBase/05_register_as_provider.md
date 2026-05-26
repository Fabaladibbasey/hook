# Registering as a provider

## Steps

1. **Start.** Message the bot with what you offer — "I'm a plumber", "I offer carpentry", or `REGISTER`.
2. **Confirm services.** The bot confirms the service slug(s). You can list up to 5 services on one listing.
3. **Share location.** Send a WhatsApp location pin or type your address.
4. **Confirm consent.** The bot asks whether the platform may share your phone number with matched clients. You can say no — in that case clients reach you only through the encrypted chat link.
5. **You are listed for 24 hours.** Nearby clients picking your service may pick you; the bot pings you when they do.

## Heartbeat

Your listing extends each time you continue a registration step or send a funnel command. Off-topic messages do not extend it. If you go quiet for the retention window, the listing flag-expires and is eventually hard-deleted.

## Commands you can send

- `LEAVE` — when sent in reply to a listing acknowledgement, unlists and deletes your aggregate statistics.
- `CANCEL` — abandon an in-progress registration draft.
- `I offer <service>` — add another service to your listing.

## Visibility

When you are picked, the client's WhatsApp number is shared with you in full. The request text and the client's stated address are also shared so you can plan the visit. The platform does not share other client metadata.
