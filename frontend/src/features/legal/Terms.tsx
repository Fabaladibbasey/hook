import LegalLayout from "./LegalLayout";
import { WhatsAppContact } from "./SupportContact";
import { RETENTION_DAYS_LABEL, LEGAL_EFFECTIVE_DATE } from "./constants";

export default function Terms() {
  return (
    <LegalLayout title="Terms of Service" effective={LEGAL_EFFECTIVE_DATE}>
      <p>
        These Terms govern your use of Hook (“Hook”, “we”, “us”), a WhatsApp-based platform that
        connects clients with nearby service providers. By using Hook you agree to these Terms. If
        you do not agree, do not use the Service.
      </p>

      <h2 id="section-1">1. Eligibility</h2>
      <p>
        You may use Hook only if you can form a binding contract under the laws of The Gambia. You
        must be at least 18 years of age, or 13 years or older with verifiable consent from a
        parent or legal guardian. You are responsible for the content you send through the
        Service.
      </p>

      <h2 id="section-2">2. Accounts and identifiers</h2>
      <p>
        Hook has no sign-up, password, or login. You are identified by the phone number you
        message us from on WhatsApp. Re-using the same number resumes any prior context tied to
        that number.
      </p>

      <h2 id="section-3">3. Roles</h2>
      <ul>
        <li>
          <strong>Clients</strong> submit a service request via WhatsApp and may chat with matched
          providers.
        </li>
        <li>
          <strong>Providers</strong> register the services they offer, their location, and a
          phone-share consent flag. Provider listings expire 24 hours after registration or last
          activity unless refreshed, and the listing record is permanently deleted{" "}
          {RETENTION_DAYS_LABEL} after expiry (or immediately on <code>LEAVE</code>).
        </li>
      </ul>

      <h2 id="section-4">4. Free service</h2>
      <p>
        Hook charges no fees and processes no payments. Any payment between a client and a
        provider is solely between them; Hook is not a party to that transaction.
      </p>

      <h2 id="section-5">5. Acceptable use</h2>
      <p>You agree not to use Hook to:</p>
      <ul>
        <li>send unlawful, infringing, fraudulent, abusive, or harassing content;</li>
        <li>impersonate any person or misrepresent your affiliation;</li>
        <li>request or offer services that are illegal in The Gambia;</li>
        <li>reverse-engineer, scrape, or interfere with the Service;</li>
        <li>circumvent rate limits or the bilateral phone-share consent gate.</li>
      </ul>

      <h2 id="section-6">6. Phone-number sharing</h2>
      <p>
        Direct phone numbers are exchanged between a client and a matched provider <em>only</em>{" "}
        when both parties have explicitly opted in (the client at intake and the provider at
        registration). If either party opts out, communication is routed through an ephemeral
        end-to-end-encrypted chat link instead.
      </p>

      <h2 id="section-7">7. Chat sessions</h2>
      <ul>
        <li>
          Chats are end-to-end encrypted (P-256 ECDH key agreement, HKDF-SHA-256 key derivation,
          AES-256-GCM message encryption). Hook stores ciphertext only and cannot read your
          messages.
        </li>
        <li>A chat ends after 30 minutes of inactivity.</li>
        <li>A chat hard-expires 24 hours after creation.</li>
        <li>
          {RETENTION_DAYS_LABEL} after expiry, all chat session data — ciphertext, nonces, device
          keys, participants, and access logs — is permanently deleted from our servers.
        </li>
        <li>
          Encryption keys live on your device only. Clearing your browser storage will permanently
          lose access to prior messages.
        </li>
      </ul>

      <h2 id="section-8">8. Provider relationship</h2>
      <p>
        Providers are independent third parties, not employees, agents, or partners of Hook. Hook
        does not vet, certify, or guarantee any provider, their qualifications, the quality of
        their work, their pricing, or any outcome. Engagements between client and provider are
        bilateral and at your own risk.
      </p>

      <h2 id="section-9">9. Rate limits</h2>
      <p>
        To prevent spam, Hook applies per-phone-number limits: a 3-message burst per 5 seconds, 30
        messages per hour, up to 20 new service requests per day, and up to 10 availability
        registrations or updates per day. Exceeding these limits results in temporary throttling.
      </p>

      <h2 id="section-10">10. AI assistance</h2>
      <p>
        Hook uses a self-hosted AI model for intent detection, language detection, and reply
        generation. AI output may be inaccurate, incomplete, or unavailable; if it fails or
        refuses, the bot reply is dropped rather than shown. Do not rely on AI replies for
        safety-critical decisions.
      </p>

      <h2 id="section-11">11. Geocoding</h2>
      <p>
        Address strings are reverse-geocoded via the Google Maps Geocoding API. Coordinates and
        addresses may be inaccurate. Distance-based match suggestions are provided as a
        convenience and are not guarantees.
      </p>

      <h2 id="section-12">12. Termination</h2>
      <p>
        You may stop using Hook at any time. Providers may unlist themselves immediately by
        replying <code>LEAVE</code> on WhatsApp. We may suspend or remove access for breach of
        these Terms or for unlawful use.
      </p>

      <h2 id="section-13">13. Disclaimer of warranties</h2>
      <p>
        Hook is provided “as is” and “as available” without warranties of any kind. To the maximum
        extent permitted by law, we disclaim implied warranties of merchantability, fitness for a
        particular purpose, and non-infringement.
      </p>

      <h2 id="section-14">14. Limitation of liability</h2>
      <p>
        To the fullest extent permitted by law, Hook is not liable for any indirect, incidental,
        special, consequential, or punitive damages, nor for any loss of data, revenue, profits,
        or goodwill, arising out of or related to your use of the Service. Our aggregate
        liability is capped at the amount you paid us in the 12 months preceding the claim, which
        is zero because the Service is free.
      </p>

      <h2 id="section-15">15. Indemnity</h2>
      <p>
        You agree to indemnify and hold Hook harmless from claims, losses, and expenses
        (including reasonable legal fees) arising from your use of the Service or your breach of
        these Terms.
      </p>

      <h2 id="section-16">16. Changes to these Terms</h2>
      <p>
        We may update these Terms from time to time. The effective date at the top will reflect
        any change. Continued use after the effective date constitutes acceptance.
      </p>

      <h2 id="section-17">17. Governing law</h2>
      <p>
        These Terms are governed by the laws of The Gambia. Any dispute arising from these Terms
        or the Service is subject to the exclusive jurisdiction of the courts of Banjul, The
        Gambia.
      </p>

      <h2 id="section-18">18. Contact</h2>
      <p>
        Questions about these Terms — message us on WhatsApp: <WhatsAppContact />.
      </p>
    </LegalLayout>
  );
}
