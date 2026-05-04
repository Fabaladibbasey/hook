namespace Hook.Features.Ai.Models;

public sealed record IntentDetectionResult(
    IntentKind Intent,
    double Confidence,
    string LanguageCode,
    string? Notes)
{
    /// <summary>
    /// True only when the LLM both picked a known, non-Unknown intent AND reported
    /// confidence at or above the supplied floor. The router falls back to its
    /// disambiguation path when this returns false.
    /// </summary>
    public bool IsActionable(double minConfidence) =>
        Intent != IntentKind.Unknown && Confidence >= minConfidence;
}
