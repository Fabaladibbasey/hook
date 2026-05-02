using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hook.Features.Ai.Models;
using Hook.Features.Ai.Prompts;
using Hook.Features.Observability;
using Microsoft.Extensions.Options;

namespace Hook.Features.Ai;

public sealed class OllamaConversationAi(
    HttpClient httpClient,
    IOptions<OllamaOptions> options,
    ILogger<OllamaConversationAi> logger) : IConversationAi
{
    public const string HttpClientName = "ai.ollama";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<IntentDetectionResult> DetectIntentAsync(string userMessage, CancellationToken ct = default)
    {
        var schema = new
        {
            type = "object",
            properties = new
            {
                intent = new { type = "string" },
                confidence = new { type = "number" },
                language = new { type = "string" },
                notes = new { type = "string" }
            },
            required = new[] { "intent", "confidence", "language" }
        };

        using var json = await CallJsonAsync(AiPrompts.IntentSystem, userMessage, schema, ct);
        var root = json.RootElement;
        var intentText = root.GetProperty("intent").GetString() ?? "Unknown";
        var confidence = root.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0;
        var language = root.TryGetProperty("language", out var l) ? l.GetString() ?? "en" : "en";
        var notes = root.TryGetProperty("notes", out var n) ? n.GetString() : null;

        if (!Enum.TryParse<IntentKind>(intentText, ignoreCase: true, out var intent))
            intent = IntentKind.Unknown;

        return new IntentDetectionResult(intent, confidence, language, notes);
    }

    public async Task<ServiceExtractionResult> ExtractServicesAsync(string userMessage, CancellationToken ct = default)
    {
        var schema = new
        {
            type = "object",
            properties = new
            {
                slugs = new { type = "array", items = new { type = "string" } }
            },
            required = new[] { "slugs" }
        };

        using var json = await CallJsonAsync(AiPrompts.ServiceExtractionSystem, userMessage, schema, ct);
        var slugs = json.RootElement.GetProperty("slugs")
            .EnumerateArray()
            .Select(s => s.GetString() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        return new ServiceExtractionResult(slugs);
    }

    public async Task<ServiceJudgeResult> JudgeServiceMatchAsync(
        string proposedSlug,
        IReadOnlyList<string> candidateSlugs,
        CancellationToken ct = default)
    {
        var schema = new
        {
            type = "object",
            properties = new
            {
                matchedSlug = new { type = "string" },
                isNew = new { type = "boolean" },
                proposedSlug = new { type = "string" }
            },
            required = new[] { "isNew" }
        };

        var prompt = $$"""
            Proposed slug: {{proposedSlug}}
            Candidate slugs: {{string.Join(", ", candidateSlugs)}}
            """;

        using var json = await CallJsonAsync(AiPrompts.ServiceJudgeSystem, prompt, schema, ct);
        var root = json.RootElement;

        var matched = root.TryGetProperty("matchedSlug", out var m) ? m.GetString() : null;
        var isNew = root.TryGetProperty("isNew", out var n) && n.GetBoolean();
        var proposed = root.TryGetProperty("proposedSlug", out var p) ? p.GetString() : null;

        return new ServiceJudgeResult(matched, isNew, proposed);
    }

    public async Task<string> GenerateReplyAsync(ReplyContext context, CancellationToken ct = default)
    {
        var transcript = string.Join("\n", context.RecentTurns.Select(t => $"{t.Role}: {t.Text}"));
        var facts = context.Facts is { Count: > 0 }
            ? "\nFacts:\n" + string.Join("\n", context.Facts.Select(kv => $"- {kv.Key}: {kv.Value}"))
            : string.Empty;

        var userPrompt = $"""
            Purpose: {context.Purpose}
            User language: {context.LanguageHint}
            {facts}

            Recent conversation:
            {transcript}

            Write the next reply.
            """;

        var systemPrompt = context.Purpose switch
        {
            "greeting-reply" => AiPrompts.GreetingReplySystem,
            "out-of-scope" => AiPrompts.OutOfScopeReplySystem,
            _ => AiPrompts.ReplySystem
        };

        var reply = await CallTextAsync(systemPrompt, userPrompt, ct);
        if (string.IsNullOrWhiteSpace(reply))
            throw new AiEmptyReplyException(context.Purpose);
        return reply;
    }

    public async Task<LanguageDetectionResult> DetectLanguageAsync(string userMessage, CancellationToken ct = default)
    {
        var schema = new
        {
            type = "object",
            properties = new
            {
                language = new { type = "string" },
                confidence = new { type = "number" }
            },
            required = new[] { "language", "confidence" }
        };

        using var json = await CallJsonAsync(AiPrompts.LanguageDetectionSystem, userMessage, schema, ct);
        var root = json.RootElement;
        var lang = root.GetProperty("language").GetString() ?? "en";
        var conf = root.GetProperty("confidence").GetDouble();
        return new LanguageDetectionResult(lang, conf);
    }

    private async Task<JsonDocument> CallJsonAsync(string systemInstruction, string userText, object responseSchema, CancellationToken ct)
    {
        var raw = await CallAsync(systemInstruction, userText, responseSchema, ct);
        return JsonDocument.Parse(raw);
    }

    private Task<string> CallTextAsync(string systemInstruction, string userText, CancellationToken ct) =>
        CallAsync(systemInstruction, userText, responseSchema: null, ct);

    private async Task<string> CallAsync(
        string systemInstruction,
        string userText,
        object? responseSchema,
        CancellationToken ct)
    {
        var opts = options.Value;

        var body = new Dictionary<string, object?>
        {
            ["model"] = opts.Model,
            ["stream"] = false,
            ["messages"] = new[]
            {
                new { role = "system", content = systemInstruction },
                new { role = "user", content = userText }
            },
            ["options"] = new { temperature = opts.Temperature }
        };

        if (responseSchema is not null)
            body["format"] = responseSchema;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };

        var sw = Stopwatch.StartNew();
        var outcome = "ok";
        try
        {
            using var response = await httpClient.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                outcome = "http_error";
                logger.LogError("Ollama call failed status={Status} body={Body}", (int)response.StatusCode, text);
                response.EnsureSuccessStatusCode();
            }

            var output = ExtractMessageContent(text);
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Ollama response (truncated): {Output}", Truncate(output, 200));

            return output;
        }
        catch
        {
            if (outcome == "ok") outcome = "exception";
            throw;
        }
        finally
        {
            sw.Stop();
            HookMetrics.AiCallsTotal.Add(1,
                new KeyValuePair<string, object?>("model", opts.Model),
                new KeyValuePair<string, object?>("outcome", outcome));
            HookMetrics.AiLatencyMs.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("model", opts.Model));
        }
    }

    private static string ExtractMessageContent(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        return doc.RootElement
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
