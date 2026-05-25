namespace Hook.Features.ServiceRequest.Create.ConfirmIntent;

public sealed record ExtractConfirmIntentCommand(
    string Phone,
    string SlugAsked,
    string Text,
    DateTimeOffset DraftStampedAt,
    // Outbox forward-compat slot: positional defaults let envelopes serialised
    // before a future field is added still deserialise (CLAUDE.md "Wolverine
    // durable persistence" rule).
    string Reserved = "");
