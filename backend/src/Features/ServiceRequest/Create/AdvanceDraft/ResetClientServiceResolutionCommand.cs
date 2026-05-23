namespace Hook.Features.ServiceRequest.Create.AdvanceDraft;

// Slug-extract returned empty / failed. On non-switch the handler resets the draft to
// AwaitingService + prompts. On switch the handler only acks (user keeps current funnel).
public sealed record ResetClientServiceResolutionCommand(
    string Phone,
    bool IsSwitch,
    string Reserved = "");
