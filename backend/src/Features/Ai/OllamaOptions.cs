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
}
