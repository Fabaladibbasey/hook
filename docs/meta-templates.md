# Meta WhatsApp Templates

## provider_check_in (Utility)

**Category:** Utility — fast approval (24–48h), low cost.
**Language:** English (en).

### Body

```
Still available for {{1}}? Reply YES to stay listed or pause to take a break.
```

`{{1}}` = comma-joined services (filled at send time by the AI / dispatcher).

### Submission

1. Open Meta Business Manager → WhatsApp Manager → Message templates.
2. Create new template:
   - Name: `provider_check_in`
   - Category: Utility
   - Language: English (en)
   - Body: copy text above (no header / footer / buttons in v1).
3. Submit. Track approval status in Meta Business Manager.

### Usage

`OutboundDispatcher.SendAsync(...)` automatically picks this template when the recipient's `last_inbound_at` is older than 24h. Inside the 24h window, free-form messaging is used.

The `hook.whatsapp.outside_window_sends` metric counts template-path sends. Investigate / restructure scheduling if it consistently exceeds ~5% of total outbound.
