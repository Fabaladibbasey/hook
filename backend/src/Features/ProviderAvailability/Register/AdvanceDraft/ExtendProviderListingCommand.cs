namespace Hook.Features.ProviderAvailability.Register.AdvanceDraft;

// Listed provider adding more services. Empty CanonicalSlugs no-ops.
public sealed record ExtendProviderListingCommand(
    string Phone,
    IReadOnlyList<string> CanonicalSlugs,
    string Reserved = "");
