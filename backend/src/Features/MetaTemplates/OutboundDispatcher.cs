using Hook.Features.Observability;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Options;

namespace Hook.Features.MetaTemplates;

public sealed class OutboundDispatcher(
    HttpClient httpClient,
    IWhatsappClient freeForm,
    IWhatsappContactRepository contacts,
    IOptions<WhatsappOptions> whatsappOptions,
    TimeProvider clock,
    ILogger<OutboundDispatcher> logger)
{
    public const string HttpClientName = "whatsapp.outbound";

    public async Task SendAsync(PhoneNumber to, string freeFormBody, string servicesCsvForTemplate, CancellationToken ct = default)
    {
        var lastInbound = await contacts.GetLastInboundAtAsync(to.Value, ct);
        var insideWindow = lastInbound is not null && (clock.GetUtcNow() - lastInbound.Value) < TimeSpan.FromHours(24);

        if (insideWindow)
        {
            await freeForm.SendTextAsync(to, freeFormBody, ct);
            return;
        }

        HookMetrics.WhatsappOutsideWindowSends.Add(1);
        logger.LogInformation("Sending Utility template to {Phone} (outside 24h window)", to.Mask());

        var opts = whatsappOptions.Value;
        var url = $"/{opts.GraphApiVersion}/{opts.PhoneNumberId}/messages";
        var payload = ProviderCheckInTemplate.BuildPayload(to.Value, servicesCsvForTemplate);

        using var response = await httpClient.PostAsJsonAsync(url, payload, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Template send failed {Status}: {Body}", (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }
    }
}
