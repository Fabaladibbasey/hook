import LegalPage, { SUPPORT_EMAIL } from "./LegalPage";
import { RETENTION_DAYS_LABEL } from "../legal/RetentionDays";

const supportMailto = `mailto:${SUPPORT_EMAIL}`;

export default function Privacy() {
  return (
    <LegalPage title="Privacy Policy" effective="2026-05-06">
      <p>
        This policy describes what information Hook (“Hook”, “we”, “us”) collects, how we use it,
        and the choices you have. Contact:{" "}
        <a href={supportMailto}>
          <code>{SUPPORT_EMAIL}</code>
        </a>
        .
      </p>

      <h2 id="section-1">1. What we collect</h2>
      <ul>
        <li>
          <strong>Phone number</strong> — sent to us when you message Hook on WhatsApp.
        </li>
        <li>
          <strong>Service request text</strong> you type on WhatsApp (free form, up to 2,000
          characters).
        </li>
        <li>
          <strong>Location</strong> — coordinates and reverse-geocoded address you share or type,
          stored as a PostGIS point (SRID 4326).
        </li>
        <li>
          <strong>WhatsApp message metadata</strong> — message id, type (text, location, image,
          audio, document, interactive, reaction), and timestamp. The raw inbound JSON is parsed
          in memory and not persisted to a database; structured fields (id, type, timestamp,
          sender) appear in our application logs subject to the 14-day rotation in Section 6.
        </li>
        <li>
          <strong>Provider data</strong> — phone, services offered (up to 5 slugs), location,
          phone-share consent flag, last-active time, and aggregated interaction stats.
        </li>
        <li>
          <strong>Chat data</strong> — your participant’s P-256 public key (one row in
          {" "}<code>chat_participants</code>) and the ciphertext + nonce of each message (one row
          in <code>chat_messages</code>). All chat data is encrypted on your device with
          AES-256-GCM under a key derived from ECDH; we cannot read message contents. We also keep
          a chat access log containing IP address and basic device information.
        </li>
        <li>
          <strong>Feedback</strong> — prompts shown to you, your replies, and timestamps.
        </li>
      </ul>

      <h2 id="section-2">2. What we do not see</h2>
      <ul>
        <li>
          <strong>Plaintext chat content.</strong> Real-time chat is end-to-end encrypted using
          P-256 ECDH + HKDF-SHA-256 + AES-256-GCM. Encryption keys never leave your device. Hook
          stores one ciphertext + nonce per message; the keys to decrypt it never reach our servers.
        </li>
        <li>Passwords — Hook has no password system.</li>
        <li>Payment data — Hook does not process payments.</li>
        <li>
          Demographic data, contact lists, or content other than what you actively send via
          WhatsApp.
        </li>
      </ul>

      <h2 id="section-3">3. Why we use it</h2>
      <ul>
        <li>To match clients to nearby providers by proximity.</li>
        <li>To send WhatsApp replies and ephemeral chat links.</li>
        <li>To prevent abuse via per-phone rate limits and prompt-safety filters.</li>
        <li>To operate the Service — application logs, metrics, and debugging.</li>
      </ul>

      <h2 id="section-4">4. Legal basis</h2>
      <p>
        Performance of a contract (operating the Service you initiated by messaging us) and our
        legitimate interest in service security, spam prevention, and abuse mitigation.
      </p>

      <h2 id="section-5">5. Sharing with third parties</h2>
      <ul>
        <li>
          <strong>WhatsApp / Meta Platforms, Inc.</strong> — to deliver and receive WhatsApp
          messages. Messages on the WhatsApp channel are handled in plaintext by us and by Meta as
          required by the WhatsApp Business Platform.
        </li>
        <li>
          <strong>Google LLC (Google Maps Geocoding API)</strong> — address strings sent for
          reverse geocoding.
        </li>
        <li>
          <strong>Self-hosted infrastructure</strong> — Postgres database, Caddy (TLS reverse
          proxy), Seq (structured logs), Prometheus (metrics). All run on infrastructure we
          control.
        </li>
        <li>
          <strong>Self-hosted AI (Ollama)</strong> — your message text is sent to a local AI model
          on our server for intent and language detection and reply generation. It is not shared
          with cloud AI providers.
        </li>
      </ul>
      <p>
        We do not sell your data. We do not use third-party advertising or analytics trackers.
      </p>

      <h2 id="section-6">6. Retention and automatic deletion</h2>
      <p>
        Hook automatically deletes user-related records {RETENTION_DAYS_LABEL} after they become
        eligible for deletion. A daily background job sweeps the database and removes any row
        whose retention window has elapsed. Deletion is permanent except for residual copies in
        encrypted database backups (see the Database backups bullet below).
      </p>
      <ul>
        <li>
          <strong>Service requests</strong> (phone, location, free-text description) —
          hard-deleted {RETENTION_DAYS_LABEL} after creation, once the request has been closed.
          Open and matched requests are retained until they close.
        </li>
        <li>
          <strong>WhatsApp contact records</strong> (phone + last-inbound timestamp, used for
          message-template rules) — deleted {RETENTION_DAYS_LABEL} after the last inbound
          WhatsApp message.
        </li>
        <li>
          <strong>Provider listings</strong> — flag-expire 24 hours after registration or last
          activity; the listing row is hard-deleted {RETENTION_DAYS_LABEL} after expiry if you do
          not refresh, or immediately when you reply <code>LEAVE</code> on WhatsApp (along with
          the aggregated provider statistics tied to that phone).
        </li>
        <li>
          <strong>Chat sessions, messages (ciphertext and nonce), participant public keys,
          participants, and chat access logs</strong> (IP + basic device info) — flag-expire 24
          hours after the chat is created; all related rows are hard-deleted{" "}
          {RETENTION_DAYS_LABEL} after expiry. After that point the messages are permanently
          unrecoverable, even by you.
        </li>
        <li>
          <strong>Match feedback rows</strong> (the prompt we sent you, your short answer, and
          timestamps) — kept {RETENTION_DAYS_LABEL} from the prompt time, then hard-deleted.
          Provider trust scores derived from your feedback live in <em>aggregate provider
          statistics</em> (see below) and are recomputed in place — deleting the feedback row
          does not erase the score.
        </li>
        <li>
          <strong>Drafts</strong> (in-progress service requests, provider registrations,
          disambiguation state) — deleted when you complete or cancel the flow, or{" "}
          {RETENTION_DAYS_LABEL} after the last activity on the draft, whichever comes first.
        </li>
        <li>
          <strong>Geocoding cache</strong> (address strings and coordinates) — deleted{" "}
          {RETENTION_DAYS_LABEL} after the entry was fetched.
        </li>
        <li>
          <strong>Application logs</strong> — 14 days (rolling file).
        </li>
        <li>
          <strong>Database backups</strong> — 14 days. Records you delete or that expire under
          the schedule above may persist in encrypted backups for up to 14 days after deletion
          before the backup itself rotates out.
        </li>
      </ul>
      <p>
        The following record is kept indefinitely because it supports the matching engine and
        provider trust history. It contains no chat content and no free-text personal
        information.
      </p>
      <ul>
        <li>
          <strong>Aggregated provider statistics</strong> (interaction and feedback counters keyed
          by provider phone) — used to rank providers fairly across requests. Removed immediately
          when a provider replies <code>LEAVE</code> on WhatsApp.
        </li>
      </ul>
      <p>
        You can request earlier deletion of any of your data at any time — see Section 8.
      </p>

      <h2 id="section-7">7. Security</h2>
      <ul>
        <li>
          End-to-end encryption for chat content (P-256 ECDH + HKDF-SHA-256 + AES-256-GCM with a
          per-message random nonce).
        </li>
        <li>TLS in transit (managed by Caddy with automatic ACME renewal).</li>
        <li>Per-phone rate limits and prompt-safety guards on AI input.</li>
        <li>Stateless, randomly generated 32-byte chat tokens bound to a single chat session.</li>
      </ul>

      <h2 id="section-8">8. Your choices and rights</h2>
      <ul>
        <li>
          <strong>Provider unlisting</strong> — reply <code>LEAVE</code> on WhatsApp to remove
          your listing and aggregated stats immediately.
        </li>
        <li>
          <strong>Early deletion request</strong> — in addition to the automatic schedule in
          Section 6, you can ask us to delete any of your data sooner by emailing{" "}
          <a href={supportMailto}>
            <code>{SUPPORT_EMAIL}</code>
          </a>{" "}
          from the WhatsApp number tied to your data. We will delete it across all live tables;
          backups rotate out within 14 days.
        </li>
        <li>
          <strong>Access and correction</strong> — email{" "}
          <a href={supportMailto}>
            <code>{SUPPORT_EMAIL}</code>
          </a>{" "}
          for a copy of your data or to correct an inaccuracy.
        </li>
        <li>
          <strong>Local data</strong> — clearing your browser storage deletes your chat keypair;
          you will lose access to past chat content.
        </li>
      </ul>

      <h2 id="section-9">9. International transfers</h2>
      <p>
        Some processors (Meta, Google) operate in jurisdictions outside The Gambia. Their own
        privacy policies apply to processing they perform on our behalf.
      </p>

      <h2 id="section-10">10. Children</h2>
      <p>
        Hook is not directed at children under 13. We do not knowingly collect data from children.
        If you believe a minor has used the Service, contact{" "}
        <a href={supportMailto}>
          <code>{SUPPORT_EMAIL}</code>
        </a>
        .
      </p>

      <h2 id="section-11">11. Cookies and local storage</h2>
      <p>
        Hook does not set tracking cookies. The chat client uses browser{" "}
        <code>localStorage</code> to keep your chat encryption keypair (key like{" "}
        <code>hook:chat:&lt;chatId&gt;:keypair:v2</code>). Clearing browser storage deletes it.
      </p>

      <h2 id="section-12">12. Changes to this policy</h2>
      <p>We may update this policy. The effective date at the top will reflect any change.</p>

      <h2 id="section-13">13. Contact</h2>
      <p>
        Questions or requests:{" "}
        <a href={supportMailto}>
          <code>{SUPPORT_EMAIL}</code>
        </a>
        .
      </p>
    </LegalPage>
  );
}
