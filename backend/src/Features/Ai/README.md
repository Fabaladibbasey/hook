# Ai — Ollama wiring + runbook

Ollama is **mandatory** in this codebase — the previous dev stub was removed. `AiReplyHelper.TryGenerateAsync` drops the message on failure; `AiReplyHelper.TryGenerateOrFallbackAsync` emits a deterministic plain-text fallback for the user-facing critical paths (`PresentMatchesHandler`).

## Production override

For warm-always production hosts (sufficient RAM to hold qwen2.5:3b in memory continuously), override:

```jsonc
"Ollama": {
  "KeepAlive": "-1"   // never unload the model
}
```

Eliminates the 20-30s cold-load whenever traffic gaps exceed the default `30m`. Trade-off: ~2 GB RAM held by `ollama serve` continuously.

## Tunables

| Key | Shipped (`appsettings.json`) | Notes |
| --- | --- | --- |
| `Ollama:TimeoutSeconds` | 120 | `HttpClient` ceiling for an individual Ollama call. |
| `Ollama:ReadinessProbeTimeoutSeconds` | 10 | `/readyz` strict probe budget (overrides `OllamaOptions` default of `2` for prod). |
| `Ollama:ReadinessCacheSeconds` | 60 | `/readyz` cache window — sparse traffic at 10s paid cold-load every minute. |
| `Ollama:KeepAlive` | `30m` | Ollama model-eviction window. `-1` = never unload. |
| `Ollama:MaxOutputTokens.Intent/Extract/Judge/Eta/Reply/Language` | 60/120/60/60/200/30 | Per-task `num_predict` cap. Structured-output (JSON-schema) calls must have enough headroom to fit the expected object — truncated JSON will fail to parse. Raise if a prompt grows. |
