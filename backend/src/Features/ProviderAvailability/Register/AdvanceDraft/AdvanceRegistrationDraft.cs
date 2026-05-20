using Hook.Features.ProviderAvailability.Register.ExtractServices;

namespace Hook.Features.ProviderAvailability.Register.AdvanceDraft;

// Empty CanonicalSlugs signals the extract returned no actionable slugs —
// AdvanceRegistrationDraftHandler resets the draft and prompts (NewRegistration)
// or no-ops (AddToExisting / AppendToDraft / AppendToAddDraft).
public sealed record AdvanceRegistrationDraft(
    string Phone,
    IReadOnlyList<string> CanonicalSlugs,
    RegistrationExtractMode Mode,
    string Reserved = "");
