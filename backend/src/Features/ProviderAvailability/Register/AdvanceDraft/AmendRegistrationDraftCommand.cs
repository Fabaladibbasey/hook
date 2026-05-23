namespace Hook.Features.ProviderAvailability.Register.AdvanceDraft;

// Bracket-style append into a ConfirmServices new-registration draft.
public sealed record AmendRegistrationDraftCommand(
    string Phone,
    IReadOnlyList<string> CanonicalSlugs,
    string Reserved = "");
