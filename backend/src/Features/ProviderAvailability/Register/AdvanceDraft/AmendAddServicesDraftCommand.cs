namespace Hook.Features.ProviderAvailability.Register.AdvanceDraft;

// Bracket-style append into a ConfirmAddServices add-to-existing draft.
public sealed record AmendAddServicesDraftCommand(
    string Phone,
    IReadOnlyList<string> CanonicalSlugs,
    string Reserved = "");
