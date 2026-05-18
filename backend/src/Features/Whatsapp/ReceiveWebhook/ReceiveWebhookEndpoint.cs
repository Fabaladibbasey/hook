using System.Text.Json;
using Hook.Features.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

public static class ReceiveWebhookEndpoint
{
    public const string Path = "/webhooks/whatsapp";

    public static IEndpointRouteBuilder MapWhatsappWebhook(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup(Path);

        group.MapGet("", HandleVerification);
        group.MapPost("", HandleInbound)
            .RequireRateLimiting(RateLimitingServiceCollectionExtensions.WebhookConcurrencyPolicy);

        return routes;
    }

    private static IResult HandleVerification(
        HttpRequest request,
        IOptions<WhatsappOptions> options,
        ILogger<WhatsappWebhookLog> logger)
    {
        var mode = request.Query["hub.mode"].ToString();
        var token = request.Query["hub.verify_token"].ToString();
        var challenge = request.Query["hub.challenge"].ToString();

        if (mode == "subscribe" && token == options.Value.VerifyToken)
        {
            logger.LogInformation("WhatsApp webhook verification succeeded");
            return Results.Text(challenge);
        }

        logger.LogWarning("WhatsApp webhook verification failed (mode={Mode})", mode);
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    [RequestSizeLimit(64 * 1024)]
    private static async Task<IResult> HandleInbound(
        HttpRequest request,
        IOptions<WhatsappOptions> options,
        IInboundDedup dedup,
        IMessageBus bus,
        ILogger<WhatsappWebhookLog> logger,
        CancellationToken ct)
    {
        request.EnableBuffering();

        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        var body = ms.ToArray();
        request.Body.Position = 0;

        var signature = request.Headers["X-Hub-Signature-256"].ToString();
        if (!SignatureValidator.IsValid(signature, body, options.Value.AppSecret))
        {
            logger.LogWarning("WhatsApp webhook signature validation failed");
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        IReadOnlyList<Models.InboundMessage> messages;
        try
        {
            using var doc = JsonDocument.Parse(body);
            messages = WebhookParser.Parse(doc.RootElement);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Malformed WhatsApp webhook payload");
            return Results.Ok();
        }

        foreach (var message in messages)
        {
            if (!dedup.TryAcquire(message.MessageId))
            {
                logger.LogDebug("Skipping duplicate WhatsApp message {MessageId}", message.MessageId);
                continue;
            }

            logger.LogInformation(
                "Inbound WhatsApp message {MessageId} from {From} kind={Kind}",
                message.MessageId,
                message.From.Mask(),
                message.Kind);

            await bus.PublishAsync(new InboundMessageReceived(message));
        }

        return Results.Ok();
    }

    public sealed class WhatsappWebhookLog;
}
