using System.Diagnostics;
using System.Text.Encodings.Web;
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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Allow fence tags (<user_input>…</user_input>) to appear literally in the serialized
        // body — the body goes only to the local Ollama endpoint, never to a browser.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public Task PingAsync(CancellationToken ct = default) =>
        DetectIntentCoreAsync("ping", ct);

    public Task<IntentDetectionResult> DetectIntentAsync(string userMessage, CancellationToken ct = default) =>
        TryCallAsync(AiStage.Classify, IntentFallback,
            () => DetectIntentCoreAsync(userMessage, ct), ct);

    private static readonly IntentDetectionResult IntentFallback =
        new(IntentKind.Unknown, 0, "en", "exception");
    private static readonly ServiceExtractionResult ExtractFallback = new([]);

    private async Task<IntentDetectionResult> DetectIntentCoreAsync(string userMessage, CancellationToken ct)
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

        using var json = await CallJsonAsync(
            AiPrompts.IntentSystem,
            PromptSafety.Fence(userMessage, options.Value.MaxUserInputChars),
            schema, options.Value.MaxOutputTokens.Intent, ct);
        var root = json.RootElement;
        var intentText = root.GetProperty("intent").GetString() ?? "";
        var confidence = root.TryGetProperty("confidence", out var c) ? Math.Clamp(c.GetDouble(), 0, 1) : 0;
        var language = root.TryGetProperty("language", out var l) ? l.GetString() ?? "en" : "en";
        var notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? string.Empty : string.Empty;

        // Strict ordinal match against the enum's named members. No ignoreCase fuzz, no
        // fallback for unknown labels — anything we don't recognise lands as Unknown,
        // which the router routes to the disambiguation flow.
        var intent = IntentKind.Unknown;
        foreach (var name in Enum.GetNames<IntentKind>())
        {
            if (string.Equals(name, intentText, StringComparison.Ordinal))
            {
                intent = Enum.Parse<IntentKind>(name);
                break;
            }
        }

        return new IntentDetectionResult(intent, confidence, language, notes);
    }

    public Task<ServiceExtractionResult> ExtractServicesAsync(string userMessage, CancellationToken ct = default) =>
        TryCallAsync(AiStage.ExtractServices, ExtractFallback,
            () => ExtractServicesCoreAsync(userMessage, ct), ct);

    private async Task<ServiceExtractionResult> ExtractServicesCoreAsync(string userMessage, CancellationToken ct)
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

        using var json = await CallJsonAsync(
            AiPrompts.ServiceExtractionSystem,
            PromptSafety.Fence(userMessage, options.Value.MaxUserInputChars),
            schema, options.Value.MaxOutputTokens.Extract, ct);
        var slugs = json.RootElement.GetProperty("slugs")
            .EnumerateArray()
            .Select(s => s.GetString() ?? string.Empty)
            .Where(PromptSafety.LooksLikeSlug)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        return new ServiceExtractionResult(slugs);
    }

    public Task<ServiceJudgeResult> JudgeServiceMatchAsync(
        string proposedSlug,
        IReadOnlyList<string> candidateSlugs,
        CancellationToken ct = default) =>
        TryCallAsync(AiStage.JudgeMatch,
            new ServiceJudgeResult(string.Empty, IsNew: true, ProposedSlug: proposedSlug),
            () => JudgeServiceMatchCoreAsync(proposedSlug, candidateSlugs, ct), ct);

    private async Task<ServiceJudgeResult> JudgeServiceMatchCoreAsync(
        string proposedSlug,
        IReadOnlyList<string> candidateSlugs,
        CancellationToken ct)
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

        using var json = await CallJsonAsync(AiPrompts.ServiceJudgeSystem, prompt, schema, options.Value.MaxOutputTokens.Judge, ct);
        var root = json.RootElement;

        var matched = root.TryGetProperty("matchedSlug", out var m) ? m.GetString() ?? string.Empty : string.Empty;
        var isNew = root.TryGetProperty("isNew", out var n) && n.GetBoolean();

        // The DB is the only ground truth for the slug taxonomy. If the LLM returns a slug
        // outside the candidate list we passed in, treat the proposal as new and let the
        // caller decide what to do with it (SlugResolver creates the canonical row from the
        // user's normalized slug, never from the LLM's string).
        if (matched.Length > 0 && !candidateSlugs.Contains(matched, StringComparer.Ordinal))
        {
            matched = string.Empty;
            isNew = true;
        }

        return new ServiceJudgeResult(matched, isNew, ProposedSlug: proposedSlug);
    }

    public Task<string?> JudgeParentSlugAsync(
        string proposedSlug,
        IReadOnlyList<string> rootCandidates,
        IReadOnlyList<string> rawExamples,
        CancellationToken ct = default) =>
        TryCallAsync<string?>(AiStage.JudgeParent, null,
            () => JudgeParentSlugCoreAsync(proposedSlug, rootCandidates, rawExamples, ct), ct);

    private async Task<string?> JudgeParentSlugCoreAsync(
        string proposedSlug,
        IReadOnlyList<string> rootCandidates,
        IReadOnlyList<string> rawExamples,
        CancellationToken ct)
    {
        if (rootCandidates.Count == 0) return null;

        var schema = new
        {
            type = "object",
            properties = new
            {
                parentSlug = new { type = new[] { "string", "null" } }
            },
            required = new[] { "parentSlug" }
        };

        // Each raw example arrives from anonymous WhatsApp inbound (SlugResolver →
        // RememberRawExample); fence individually so role tokens / chat-control
        // bytes inside any single entry cannot escape into sibling fences or the
        // surrounding system prompt.
        var examples = rawExamples.Count == 0
            ? "(none)"
            : string.Join("\n", rawExamples.Take(5)
                .Select(r => PromptSafety.Fence(r, options.Value.MaxUserInputChars)));
        var prompt = $$"""
            Proposed slug: {{proposedSlug}}
            Candidate parent slugs: {{string.Join(", ", rootCandidates)}}
            Recent raw examples:
            {{examples}}
            """;

        using var json = await CallJsonAsync(AiPrompts.ParentSlugJudgeSystem, prompt, schema, options.Value.MaxOutputTokens.Judge, ct);
        var root = json.RootElement;
        if (!root.TryGetProperty("parentSlug", out var p) || p.ValueKind == JsonValueKind.Null)
            return null;

        var picked = p.GetString();
        if (string.IsNullOrWhiteSpace(picked)) return null;

        // Same DB-is-ground-truth guard as JudgeServiceMatchAsync: ignore any
        // slug not in the candidate list we passed in.
        if (!rootCandidates.Contains(picked, StringComparer.Ordinal))
        {
            logger.LogWarning("Ollama returned out-of-candidate parent {Picked} for {Proposed}", picked, proposedSlug);
            return null;
        }
        if (string.Equals(picked, proposedSlug, StringComparison.Ordinal))
            return null;

        return picked;
    }

    public async Task<string> GenerateReplyAsync(ReplyContext context, CancellationToken ct = default)
    {
        var transcript = PromptSafety.EncodeTurns(context.RecentTurns);
        var factsBlock = context.Facts is { Count: > 0 }
            ? "\nFacts (JSON object):\n" + JsonSerializer.Serialize(context.Facts)
            : string.Empty;

        var userPrompt = $"""
            Purpose: {context.Purpose}
            User language: {context.LanguageHint}
            {factsBlock}

            Recent conversation (JSON array of role+text turns):
            {transcript}

            Write the next reply.
            """;

        var systemPrompt = context.Purpose switch
        {
            "greeting-reply" => AiPrompts.GreetingReplySystem,
            "out-of-scope" => AiPrompts.OutOfScopeReplySystem,
            "match_presenter" => AiPrompts.MatchPresenterReplySystem,
            _ => AiPrompts.ReplySystem
        };

        var reply = await CallTextAsync(systemPrompt, userPrompt, options.Value.MaxOutputTokens.Reply, ct);
        if (string.IsNullOrWhiteSpace(reply))
            throw new AiEmptyReplyException(context.Purpose);
        return reply;
    }

    public Task<DateTimeOffset?> ExtractEtaAsync(string userMessage, DateTimeOffset now, CancellationToken ct = default) =>
        TryCallAsync<DateTimeOffset?>(AiStage.ExtractEta, null,
            () => ExtractEtaCoreAsync(userMessage, now, ct), ct);

    private async Task<DateTimeOffset?> ExtractEtaCoreAsync(string userMessage, DateTimeOffset now, CancellationToken ct)
    {
        var schema = new
        {
            type = "object",
            properties = new
            {
                etaUtc = new { type = new[] { "string", "null" } }
            },
            required = new[] { "etaUtc" }
        };

        // Format `referenceUtc` to match the system prompt's expected
        // "YYYY-MM-DDTHH:MM:SSZ" — the round-trip "o" specifier emits sub-second
        // precision and "+00:00" rather than "Z", which the prompt does not
        // describe and the model occasionally garbles.
        var referenceUtc = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        var prompt = $$"""
            referenceUtc: {{referenceUtc}}
            {{PromptSafety.Fence(userMessage, options.Value.MaxUserInputChars)}}
            """;

        using var json = await CallJsonAsync(AiPrompts.EtaExtractionSystem, prompt, schema, options.Value.MaxOutputTokens.Eta, ct);
        var root = json.RootElement;
        if (!root.TryGetProperty("etaUtc", out var etaProp) || etaProp.ValueKind == JsonValueKind.Null)
            return null;

        var raw = etaProp.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var eta))
            return null;
        // Ignore past timestamps — they're useless as a recheck schedule.
        // EtaScheduleBuffer in the caller absorbs the boundary-equality case so
        // `eta == now` is treated as "right now" and still schedules.
        return eta >= now ? eta : null;
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

        using var json = await CallJsonAsync(
            AiPrompts.LanguageDetectionSystem,
            PromptSafety.Fence(userMessage, options.Value.MaxUserInputChars),
            schema, options.Value.MaxOutputTokens.Language, ct);
        var root = json.RootElement;
        var lang = root.GetProperty("language").GetString() ?? "en";
        var conf = root.GetProperty("confidence").GetDouble();
        return new LanguageDetectionResult(lang, conf);
    }

    private enum AiStage { Classify, ExtractServices, JudgeMatch, JudgeParent, ExtractEta }

    private static string TagOf(AiStage stage) => stage switch
    {
        AiStage.Classify => "classify",
        AiStage.ExtractServices => "extract-services",
        AiStage.JudgeMatch => "judge-match",
        AiStage.JudgeParent => "judge-parent",
        AiStage.ExtractEta => "extract-eta",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
    };

    // Outer-token cancellation rethrows so Wolverine's shutdown OCE policy can
    // discard cleanly. Everything else increments AiOutboundDropped(stage=…) and
    // returns the neutral fallback. Classify also increments the dedicated
    // AiClassifyFailures counter that pre-existed this consolidation — the
    // double-count between AiClassifyFailures (legacy single-metric) and
    // AiOutboundDropped(stage=classify) (new tag-set) is intentional so
    // pre-existing dashboards keep working.
    private async Task<T> TryCallAsync<T>(AiStage stage, T fallback, Func<Task<T>> work, CancellationToken ct)
    {
        try
        {
            return await work();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var tag = TagOf(stage);
            logger.LogWarning(ex, "AI {Stage} failed; returning neutral fallback", tag);
            HookMetrics.AiOutboundDropped.Add(1,
                new KeyValuePair<string, object?>("stage", tag),
                new KeyValuePair<string, object?>("reason", "exception"));
            if (stage == AiStage.Classify)
                HookMetrics.AiClassifyFailures.Add(1);
            return fallback;
        }
    }

    private async Task<JsonDocument> CallJsonAsync(string systemInstruction, string userText, object responseSchema, int numPredict, CancellationToken ct)
    {
        var raw = await CallAsync(systemInstruction, userText, responseSchema, numPredict, ct);
        return JsonDocument.Parse(raw);
    }

    private Task<string> CallTextAsync(string systemInstruction, string userText, int numPredict, CancellationToken ct) =>
        CallAsync(systemInstruction, userText, responseSchema: null, numPredict, ct);

    private async Task<string> CallAsync(
        string systemInstruction,
        string userText,
        object? responseSchema,
        int numPredict,
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
            ["options"] = new { temperature = opts.Temperature, num_predict = numPredict },
            ["keep_alive"] = opts.KeepAlive
        };

        if (responseSchema is not null)
            body["format"] = responseSchema;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };

        var sw = Stopwatch.StartNew();
        // 4xx/5xx now tag "http_error"; pre-consolidation always tagged
        // "exception". Dashboard panels may need a backfill alias.
        var outcome = "exception";
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

            outcome = "ok";
            return output;
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
