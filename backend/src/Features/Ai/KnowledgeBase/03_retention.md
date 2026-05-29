# Data retention

A daily background sweep deletes user-related records after their retention window elapses. Exact day counts are config-driven and live in our published Privacy Policy.

## What expires automatically

- **Service requests** — deleted after the retention window from the request creation timestamp.
- **WhatsApp contact records** — deleted some days after your last inbound message.
- **Provider listings** — flag-expire 24 hours after registration or last activity, then hard-deleted some days later. Replying `LEAVE` to a listing acknowledgement deletes the listing immediately.
- **Chat sessions, messages (ciphertext), public keys, participants, access logs** — flag-expire 24 hours after the chat is created, then hard-deleted some days later. After that point messages are permanently unrecoverable, even by you.
- **Drafts** (in-progress requests / registrations) — deleted when you complete or cancel the flow, or after the inactivity window.
- **Geocoding cache** — deleted some days after the entry was fetched.

## What is kept longer

Aggregated provider statistics (anonymous counters keyed by provider phone) are kept indefinitely to rank providers fairly across requests. Removed immediately when a provider replies `LEAVE`.
