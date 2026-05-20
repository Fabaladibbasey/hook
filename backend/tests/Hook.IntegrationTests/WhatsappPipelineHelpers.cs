using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;

namespace Hook.IntegrationTests;

public sealed record OutboxMessage(
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("messageId")] string MessageId);

public static class WhatsappPipelineHelpers
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<HttpResponseMessage> InjectTextAsync(
        this HttpClient client,
        string from,
        string text,
        CancellationToken ct = default) =>
        await client.PostAsJsonAsync("/dev/whatsapp/inbound",
            new { from, type = "text", text }, Json, ct);

    public static async Task<HttpResponseMessage> InjectTextAsync(
        this HttpClient client,
        string from,
        string text,
        string messageId,
        CancellationToken ct = default) =>
        await client.PostAsJsonAsync("/dev/whatsapp/inbound",
            new { from, type = "text", text, messageId }, Json, ct);

    public static async Task<HttpResponseMessage> InjectLocationAsync(
        this HttpClient client,
        string from,
        double lat,
        double lng,
        CancellationToken ct = default) =>
        await client.PostAsJsonAsync("/dev/whatsapp/inbound",
            new { from, type = "location", latitude = lat, longitude = lng }, Json, ct);

    public static async Task<IReadOnlyList<OutboxMessage>> GetOutboxAsync(
        this HttpClient client,
        CancellationToken ct = default)
    {
        var msgs = await client.GetFromJsonAsync<List<OutboxMessage>>("/dev/whatsapp/outbox", Json, ct);
        return msgs ?? [];
    }

    /// <summary>
    /// Wolverine-tracked text inject. POSTs to <c>/dev/whatsapp/inbound</c> then blocks until
    /// every cascading message the inject triggered has finished executing. After this returns,
    /// all outbound messages produced by the cascade are already in the dev outbox. Throws on
    /// timeout (default 10s).
    /// </summary>
    public static Task InjectTextAndAwaitAsync(
        this DevPipelineFixture fx,
        string from,
        string text,
        TimeSpan? timeout = null,
        CancellationToken ct = default) =>
        TrackedInjectAsync(fx, c => c.InjectTextAsync(from, text, ct), timeout);

    /// <summary>Tracked text inject with explicit messageId; identical drain semantics
    /// to the no-id overload. Throws on timeout (default 10s).</summary>
    public static Task InjectTextAndAwaitAsync(
        this DevPipelineFixture fx,
        string from,
        string text,
        string messageId,
        TimeSpan? timeout = null,
        CancellationToken ct = default) =>
        TrackedInjectAsync(fx, c => c.InjectTextAsync(from, text, messageId, ct), timeout);

    /// <summary>Tracked inject of a location reply; awaits cascade drain before returning.
    /// Throws on timeout (default 10s).</summary>
    public static Task InjectLocationAndAwaitAsync(
        this DevPipelineFixture fx,
        string from,
        double lat,
        double lng,
        TimeSpan? timeout = null,
        CancellationToken ct = default) =>
        TrackedInjectAsync(fx, c => c.InjectLocationAsync(from, lat, lng, ct), timeout);

    private static Task TrackedInjectAsync(
        DevPipelineFixture fx,
        Func<HttpClient, Task<HttpResponseMessage>> inject,
        TimeSpan? timeout)
    {
        var host = fx.Factory.Services.GetRequiredService<IHost>();
        Func<Wolverine.IMessageContext, Task> action = async _ =>
        {
            using var client = fx.Factory.CreateClient();
            (await inject(client)).EnsureSuccessStatusCode();
        };
        return host.TrackActivity()
            .Timeout(timeout ?? TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync(action);
    }

    /// <summary>
    /// Returns the most recent outbox message to <paramref name="toPhone"/> matching
    /// <paramref name="predicate"/>, or throws <see cref="InvalidOperationException"/>.
    /// After a tracked inject the cascade has already settled, so a single read
    /// suffices — no polling. <paramref name="since"/> is strict greater-than: it
    /// filters out messages produced by prior steps. <paramref name="description"/>
    /// is included in the throw message to ease test triage.
    /// </summary>
    public static async Task<OutboxMessage> ExpectOutboundAsync(
        this HttpClient client,
        string toPhone,
        Func<OutboxMessage, bool> predicate,
        DateTimeOffset? since = null,
        string? description = null,
        CancellationToken ct = default)
    {
        var outbox = await client.GetOutboxAsync(ct);
        for (var i = outbox.Count - 1; i >= 0; i--)
        {
            var m = outbox[i];
            if (m.To == toPhone && (since is null || m.At > since) && predicate(m)) return m;
        }
        throw new InvalidOperationException(
            $"No matching outbound to {toPhone}{(description is null ? "" : $" ({description})")} in outbox of {outbox.Count} messages.");
    }

    /// <summary>
    /// Polling fallback for assertions that span things Wolverine TrackActivity cannot
    /// observe (SignalR push, scheduled retention sweeps, etc.). Most pipeline tests
    /// should use the tracked inject helpers + <see cref="ExpectOutboundAsync"/>.
    /// 5s default timeout matches the typical handler-cascade ceiling on CI under the
    /// inline send path; <see cref="TrackedInjectAsync"/> uses a 30s ceiling to absorb
    /// the now-deferred outbox round-trip on AI-stage handlers. <paramref name="since"/>
    /// is strict greater-than. Throws <see cref="TimeoutException"/> on miss.
    /// </summary>
    public static async Task<OutboxMessage> WaitForOutboundAsync(
        this HttpClient client,
        string toPhone,
        Func<OutboxMessage, bool> predicate,
        TimeSpan? timeout = null,
        DateTimeOffset? since = null,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var outbox = await client.GetOutboxAsync(ct);
            var match = outbox.FirstOrDefault(m =>
                m.To == toPhone &&
                (since is null || m.At > since) &&
                predicate(m));
            if (match is not null) return match;
            await Task.Delay(25, ct);
        }
        throw new TimeoutException($"No matching outbound to {toPhone} within deadline.");
    }

    /// <summary>
    /// Polls <paramref name="condition"/> every 25 ms until it returns true. Throws
    /// <see cref="TimeoutException"/> with <paramref name="description"/> on timeout
    /// (default 5s — matches <see cref="WaitForOutboundAsync"/>).
    /// </summary>
    public static async Task WaitForConditionAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        string? description = null,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25, ct);
        }
        throw new TimeoutException(description ?? "Condition not satisfied within deadline.");
    }
}
