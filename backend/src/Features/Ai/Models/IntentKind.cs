namespace Hook.Features.Ai.Models;

// Outbox-stable contract: serialised as ordinal int through Wolverine envelopes
// and ClassifyInboundIntentCommand. Append new members only — never reorder.
// If a future change switches Wolverine/STJ to JsonStringEnumConverter, the
// member names — not the ordinals — become the on-wire contract.
public enum IntentKind
{
    Unknown = 0,
    ProviderRegistration = 1,
    ServiceRequest = 2,
    MatchSelection = 3,
    NextMatches = 4,
    IncreaseRange = 5,
    ShareContact = 6,
    Confirmation = 7,
    Rejection = 8,
    Edit = 9,
    Cancel = 10,
    FeedbackResponse = 11,
    Greeting = 12,
    NewRequest = 13,
    PlatformQuestion = 14
}
