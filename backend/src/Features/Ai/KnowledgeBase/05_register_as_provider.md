# Registering as a provider

## Steps

1. **Start.** Message the bot with what you offer — "I'm a plumber", "I offer carpentry", or `REGISTER`.
2. **Confirm services.** The bot confirms the service slug(s). You can list up to 5 services on one listing.
3. **Share location.** Send a WhatsApp location pin or type your address.
4. **Confirm consent.** The bot asks whether the platform may share your phone number with matched clients. You can say no — in that case clients reach you only through the encrypted chat link.
5. **You are listed for up to 24 hours per session.** Nearby clients picking your service may pick you; the bot pings you when they do. A little before the listing expires, the bot pings you with a refresh prompt — reply and the timer extends. Any reply from your number extends the timer; sending `LEAVE` unlists you.

## Heartbeat

Your listing extends each time you send any reply to the bot. To unlist before the 24-hour window, send `LEAVE`. The listing flag-expires if you go quiet for the full retention window and is eventually hard-deleted.

## Commands you can send

- `LEAVE` — when sent in reply to a listing acknowledgement, unlists and deletes your aggregate statistics.
- `CANCEL` — abandon an in-progress registration draft.
- `I offer <service>` — add another service to your listing.

## Visibility

When you are picked, the client's WhatsApp number is shared with you in full. The request text and the client's stated address are also shared so you can plan the visit. The platform does not share other client metadata.
