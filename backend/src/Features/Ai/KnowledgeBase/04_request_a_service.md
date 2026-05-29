# Requesting a service (client flow)

## Steps

1. **Start.** Message the bot on WhatsApp with what you need — e.g. "I need a plumber", "I need a ride", or just "REQUEST".
2. **Confirm the service.** The bot asks "Do you need <service>?" — reply `YES` or `NO`.
3. **Share location.** Send a WhatsApp location pin, or type your address. The bot geocodes the address to coordinates.
4. **Describe the job (optional).** A short free-text description helps providers respond faster.
5. **Pick a provider.** The bot lists nearby matches ranked by a combined score of distance, recent activity, and past feedback. Reply `PICK 1`, `PICK 2`, etc. — or just `1`, `2`.
6. **Connect.** The bot either shares the provider's phone number with you, or opens an ephemeral private chat link.

## Commands you can send mid-flow

- `CANCEL` — abandon the current draft and start over.
- `NEW` — close the current request and start a fresh one.
- `NEXT` — see more matches if the first batch did not fit.
- `INCREASE` — widen the search radius.
- `PICK <n>` — pick the n-th match. Plain `1` / `2` works too.

## Cost

The platform is free during launch. You only pay the provider for the work they do — Hook is not involved in that transaction.
