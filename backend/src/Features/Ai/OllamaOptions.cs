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

    // Ollama `keep_alive` request field. Holds the model in memory between calls
    // so the next inbound after an idle period hits the warm path instead of
    // paying a 20-30s reload. Accepts Ollama duration syntax: "30m", "1h",
    // "-1" (infinite), "0" (unload immediately). Default "30m" is the memory-aware
    // setting for shared dev / single-box deploys; warm-always hosts can override to "-1".
    public string KeepAlive { get; init; } = "30m";

    // Per-task `num_predict` caps. Ollama aborts generation when the cap is hit, so
    // structured-output (JSON-schema) calls must have enough headroom to fit the
    // expected object — truncated JSON will fail to parse. Numbers reflect observed
    // median + headroom from the existing prompts; raise if a prompt grows.
    public OllamaTaskBudgets MaxOutputTokens { get; init; } = new();
}

public sealed class OllamaTaskBudgets
{
    [System.ComponentModel.DataAnnotations.Range(1, 4096)] public int Intent { get; init; } = 60;
    [System.ComponentModel.DataAnnotations.Range(1, 4096)] public int Extract { get; init; } = 120;
    [System.ComponentModel.DataAnnotations.Range(1, 4096)] public int Judge { get; init; } = 60;
    [System.ComponentModel.DataAnnotations.Range(1, 4096)] public int Eta { get; init; } = 60;
    [System.ComponentModel.DataAnnotations.Range(1, 4096)] public int Reply { get; init; } = 200;
    [System.ComponentModel.DataAnnotations.Range(1, 4096)] public int Language { get; init; } = 30;
}
