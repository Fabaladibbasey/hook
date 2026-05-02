using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public static async Task<HttpResponseMessage> InjectInteractiveAsync(
        this HttpClient client,
        string from,
        string title,
        CancellationToken ct = default) =>
        await client.PostAsJsonAsync("/dev/whatsapp/inbound",
            new { from, type = "interactive", interactiveTitle = title }, Json, ct);

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

    public static async Task<OutboxMessage> WaitForOutboundAsync(
        this HttpClient client,
        string toPhone,
        Func<OutboxMessage, bool> predicate,
        TimeSpan? timeout = null,
        DateTimeOffset? since = null,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var outbox = await client.GetOutboxAsync(ct);
            var match = outbox.FirstOrDefault(m =>
                m.To == toPhone &&
                (since is null || m.At > since) &&
                predicate(m));
            if (match is not null) return match;
            await Task.Delay(100, ct);
        }
        throw new TimeoutException($"No matching outbound to {toPhone} within deadline.");
    }
}
