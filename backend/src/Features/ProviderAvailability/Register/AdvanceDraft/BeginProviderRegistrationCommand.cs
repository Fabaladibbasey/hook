namespace Hook.Features.ProviderAvailability.Register.AdvanceDraft;

// New-registration path. Empty CanonicalSlugs resets the draft and re-prompts.
public sealed record BeginProviderRegistrationCommand(
    string Phone,
    IReadOnlyList<string> CanonicalSlugs,
    string Reserved = "");
