namespace Hook.Features.ServiceRequest.Create.ConfirmIntent;

public sealed record ApplyConfirmIntentCommand(
    string Phone,
    ConfirmReplyIntent Intent,
    DateTimeOffset DraftStampedAt,
    // Outbox forward-compat slot: positional defaults let envelopes serialised
    // before a future field is added still deserialise (CLAUDE.md "Wolverine
    // durable persistence" rule).
    string Reserved = "");
