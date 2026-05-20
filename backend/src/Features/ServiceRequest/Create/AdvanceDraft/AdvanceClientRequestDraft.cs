namespace Hook.Features.ServiceRequest.Create.AdvanceDraft;

// On the non-switch path (IsSwitch=false), empty CanonicalSlug resets the draft back
// to AwaitingService. On the switch path (IsSwitch=true), empty CanonicalSlug is a
// no-op — the draft stays in its current step and the user is not interrupted.
public sealed record AdvanceClientRequestDraft(string Phone, string CanonicalSlug, bool IsSwitch, string Reserved = "");
