namespace Hook.Features.Ai;

public class OllamaOptions
{
    public const string SectionName = "Ollama";

    public string BaseUrl { get; init; } = "http://localhost:11434";

    public string Model { get; init; } = "qwen2.5:3b";

    public double Temperature { get; init; } = 0.0;

    public int TimeoutSeconds { get; init; } = 120;

    // /readyz strict probe timeout. Default 2s preserves k8s liveness strictness;
    // local CPU dev should override (e.g. 8s) to accommodate qwen2.5:3b warm-up.
    public int ReadinessProbeTimeoutSeconds { get; init; } = 2;

    // Minimum confidence the LLM must report on a classified intent before the router
    // will act on it; below this we route to the existing disambiguation flow. Matches
    // InboundRouterHandler.AmbiguityConfidenceThreshold.
    public double IntentMinConfidence { get; init; } = 0.75;

    // Hard cap on user-supplied text passed into a fenced LLM prompt. Defends against
    // prompt-stuffing / context-padding attacks.
    public int MaxUserInputChars { get; init; } = 1000;
}
