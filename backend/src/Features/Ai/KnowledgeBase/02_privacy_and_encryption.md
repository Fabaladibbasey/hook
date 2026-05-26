# Privacy and encryption

## End-to-end encrypted chat

When the bot opens a private chat link, the conversation is end-to-end encrypted using **P-256 ECDH + HKDF-SHA-256 + AES-256-GCM**. Keys are generated in your browser and never leave your device. The server stores only ciphertext + a random nonce per message; we cannot read what you write.

- The chat link is bound to a single device per participant. Opening the link on a second device rotates your session and revokes the old tab on its next action.
- Clearing browser storage deletes your local keypair. Past chat content becomes permanently unreadable.

## WhatsApp messages

Messages on the WhatsApp channel itself are handled in plaintext by us and by Meta as required by the WhatsApp Business Platform. The end-to-end encrypted layer applies to the private chat link only, not to WhatsApp.

## What we collect

Your phone number, the service request text you type, the coordinates / address you share, message metadata (id, type, timestamp), and provider listing data. When user text is included in an internal message envelope or feedback comment, phone-number-shaped digits are masked before it is persisted.

## What we do not collect

No passwords (Hook has no password system), no payment data, no demographic data, no contact lists, no tracking cookies, no third-party analytics.

## When you walk away from a chat

You do not need to close a chat manually. If both of you stop talking for a while, the bot sends a one-line reminder. If the silence continues past the configured threshold, the bot ends the chat for you. Either side can also reply END at any time to close the chat early. Every chat flag-expires automatically 24 hours after it is opened; after the hard-delete window past that point the ciphertext is permanently unrecoverable, even by you.

## Self-hosted AI

Your message text is sent to a local AI model running on our infrastructure for intent and language detection and reply generation. It is not shared with cloud AI providers (OpenAI, Anthropic, Google Gemini, etc.).
