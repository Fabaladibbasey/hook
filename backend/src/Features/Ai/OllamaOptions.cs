namespace Hook.Features.Ai;

public class OllamaOptions
{
    public const string SectionName = "Ollama";

    public string BaseUrl { get; init; } = "http://localhost:11434";

    public string Model { get; init; } = "qwen2.5:3b";

    public double Temperature { get; init; } = 0.0;

    public int TimeoutSeconds { get; init; } = 120;
}
