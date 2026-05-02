using System.Text.Json;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Options;

namespace Hook.Features.Whatsapp;

public sealed class WhatsappClient(
    HttpClient httpClient,
    IOptions<WhatsappOptions> options,
    ILogger<WhatsappClient> logger) : IWhatsappClient
{
    public const string HttpClientName = "whatsapp.cloud";

    public async Task<string> SendTextAsync(PhoneNumber to, string body, CancellationToken ct = default)
    {
        var opts = options.Value;
        var url = $"/{opts.GraphApiVersion}/{opts.PhoneNumberId}/messages";

        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = to.Value.TrimStart('+'),
            type = "text",
            text = new { preview_url = false, body }
        };

        using var response = await httpClient.PostAsJsonAsync(url, payload, ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "WhatsApp send failed for {To} status={Status} body={Body}",
                to.Mask(),
                (int)response.StatusCode,
                content);
            response.EnsureSuccessStatusCode();
        }

        var messageId = TryExtractMessageId(content);
        logger.LogInformation("Outbound WhatsApp text to {To} messageId={MessageId}", to.Mask(), messageId);
        return messageId ?? string.Empty;
    }

    private static string? TryExtractMessageId(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            return doc.RootElement.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array
                && messages.GetArrayLength() > 0
                && messages[0].TryGetProperty("id", out var idEl)
                ? idEl.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
