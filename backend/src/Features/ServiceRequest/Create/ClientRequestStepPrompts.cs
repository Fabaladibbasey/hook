namespace Hook.Features.ServiceRequest.Create;

// Single source of truth for the canonical re-prompt string per draft step.
// Used by the orchestrator's mid-flow-Q&A reprompt AND by the per-step
// handlers / ConfirmIntent apply handler when their inbound payload is the
// wrong shape — so the user always sees the same wording for "the bot is
// still waiting for X".
internal static class ClientRequestStepPrompts
{
    public static string For(ClientRequestStep step, string? draftServiceSlug = null) => step switch
    {
        ClientRequestStep.AwaitingService =>
            "What service do you need? Reply with something like \"I need a plumber\".",
        ClientRequestStep.ResolvingService =>
            "Still looking up your earlier message — one moment.",
        ClientRequestStep.ConfirmService =>
            $"Please reply YES or NO — YES to confirm {(draftServiceSlug ?? string.Empty).Replace('-', ' ')}, "
            + "NO to choose another service.",
        ClientRequestStep.AwaitingLocation =>
            "Send your location pin (or type your address).",
        ClientRequestStep.ConfirmLocation =>
            "Reply YES to confirm or send your GPS pin.",
        ClientRequestStep.AwaitingDescription =>
            "Want to add a description? Send it now or reply SKIP.",
        ClientRequestStep.AwaitingPhoneShareConsent =>
            "Should we share your phone number with selected providers? Reply YES or NO.",
        _ => "Got it — continue with the request."
    };
}
