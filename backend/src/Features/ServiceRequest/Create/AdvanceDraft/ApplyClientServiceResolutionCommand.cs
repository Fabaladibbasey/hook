namespace Hook.Features.ServiceRequest.Create.AdvanceDraft;

// Successful resolution from ExtractServicesHandler. IsSwitch=true means the user
// typed a new service mid-funnel; false is the initial StartAsync path.
public sealed record ApplyClientServiceResolutionCommand(
    string Phone,
    string CanonicalSlug,
    bool IsSwitch,
    string Reserved = "");
