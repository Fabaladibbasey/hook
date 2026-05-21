namespace Hook.Features.Geocoding.Geocode;

public enum GeocodeFlow { Client, Provider }

// PII: Phone + AddressText persist in wolverine_incoming_envelopes for the
// duration of handler retries and in wolverine_dead_letter_queue if the handler
// hard-fails. RetentionSweeper prunes DLQ at DeadLetterRetentionDays (default 7d).
// DraftStampedAt = draft.UpdatedAt at publish time; apply handlers compare against
// current draft.UpdatedAt and discard mismatches (cross-draft CANCEL+restart race).
public sealed record GeocodeAddressRequested(
    string Phone,
    string AddressText,
    GeocodeFlow Flow,
    DateTimeOffset DraftStampedAt = default,
    string Reserved = "");
