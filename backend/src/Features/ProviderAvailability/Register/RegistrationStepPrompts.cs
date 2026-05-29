namespace Hook.Features.ProviderAvailability.Register;

// Single source of truth for the canonical re-prompt string per registration
// draft step. Used by the orchestrator's mid-flow-Q&A reprompt AND the per-step
// re-prompt branches so wording stays in lockstep.
internal static class RegistrationStepPrompts
{
    public static string For(RegistrationStep step) => step switch
    {
        RegistrationStep.AwaitingServices =>
            "What service(s) do you offer? Reply with something like \"I offer plumbing\".",
        RegistrationStep.ResolvingServices =>
            "Still looking up your earlier message — one moment.",
        RegistrationStep.ConfirmServices =>
            "Reply YES to confirm or EDIT to change.",
        RegistrationStep.AwaitingLocation =>
            "Send your location pin (or type your address).",
        RegistrationStep.ConfirmLocation =>
            "Reply YES to confirm this address, or send your GPS pin instead.",
        RegistrationStep.AwaitingConsent =>
            "Share your phone with clients on match? Reply YES to share, NO to keep it private.",
        _ => "Got it — continue with the registration."
    };
}
