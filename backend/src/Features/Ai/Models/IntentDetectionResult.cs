namespace Hook.Features.Ai.Models;

public sealed record IntentDetectionResult(
    IntentKind Intent,
    double Confidence,
    string LanguageCode,
    string? Notes);
