using Hook.Features.Ai.Models;

namespace Hook.Features.Ai;

public static class QuickIntent
{
    public static IntentKind? Detect(string? text) =>
        text?.Trim().ToLowerInvariant() switch
        {
            "y" or "yes" or "yeah" or "yep" or "yup" or "ok" or "okay" or "sure" or "confirm" => IntentKind.Confirmation,
            "n" or "no" or "nope" or "nah" or "cancel" or "stop" => IntentKind.Rejection,
            _ => null
        };
}
