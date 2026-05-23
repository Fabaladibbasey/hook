using System.Text.Json;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;
using Hook.Features.Whatsapp.ReceiveWebhook;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.Whatsapp.Dev;

public static class DevWhatsappEndpoints
{
    public static IEndpointRouteBuilder MapDevWhatsapp(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/dev/whatsapp");

        group.MapGet("/status", (IOptions<DevWhatsappOptions> opts) =>
            Results.Ok(new { enabled = opts.Value.Enabled }));

        group.MapPost("/inbound", InjectInbound);
        group.MapPost("/echo", EchoOutbound);

        group.MapGet("/outbox", (IDevOutbox outbox) =>
            Results.Ok(outbox.Recent()));

        group.MapGet("/outbox/stream", StreamOutbox);

        return routes;
    }

    public sealed record InjectInboundRequest(
        string From,
        string? Text,
        string? Type,
        double? Latitude,
        double? Longitude,
        string? InteractiveTitle,
        string? MessageId = null);

    private static async Task<IResult> InjectInbound(
        InjectInboundRequest req,
        IInboundDedup dedup,
        IMessageBus bus,
        ILogger<DevWhatsappLog> logger,
        CancellationToken ct)
    {
        if (!PhoneNumber.TryParse(req.From, out var phone))
            return Results.BadRequest(new { error = "from must be E.164 (e.g. +22070000001)" });

        var kind = (req.Type ?? "text").ToLowerInvariant() switch
        {
            "text" => InboundMessageKind.Text,
            "location" => InboundMessageKind.Location,
            "interactive" => InboundMessageKind.Interactive,
            "image" => InboundMessageKind.Image,
            "audio" => InboundMessageKind.Audio,
            "document" => InboundMessageKind.Document,
            "reaction" => InboundMessageKind.Reaction,
            _ => InboundMessageKind.Text
        };

        InboundLocation? loc = kind == InboundMessageKind.Location && req.Latitude.HasValue && req.Longitude.HasValue
            ? new InboundLocation(req.Latitude.Value, req.Longitude.Value, null, null)
            : null;

        var text = kind switch
        {
            InboundMessageKind.Text => req.Text,
            InboundMessageKind.Interactive => req.InteractiveTitle ?? req.Text,
            _ => null
        };

        var messageId = string.IsNullOrWhiteSpace(req.MessageId)
            ? $"wamid.dev.in.{Guid.NewGuid():N}"
            : req.MessageId;
        var msg = new InboundMessage(
            messageId,
            phone,
            DateTimeOffset.UtcNow,
            kind,
            text,
            loc,
            JsonSerializer.Serialize(req));

        if (!dedup.TryAcquire(messageId))
            return Results.Conflict(new { error = "duplicate" });

        logger.LogInformation(
            "[DEV] Injecting inbound {MessageId} from {From} kind={Kind}",
            messageId, phone.Mask(), kind);

        await bus.PublishAsync(new RouteInboundMessageCommand(msg));
        return Results.Ok(new { messageId });
    }

    public sealed record EchoOutboundRequest(string To, string Body);

    private static async Task<IResult> EchoOutbound(
        EchoOutboundRequest req,
        IWhatsappClient client,
        CancellationToken ct)
    {
        if (!PhoneNumber.TryParse(req.To, out var to))
            return Results.BadRequest(new { error = "to must be E.164" });

        var id = await client.SendTextAsync(to, req.Body, ct);
        return Results.Ok(new { messageId = id });
    }

    private static readonly JsonSerializerOptions SseJsonOptions = JsonSerializerOptions.Web;

    private static async Task StreamOutbox(
        HttpContext http,
        IDevOutbox outbox,
        CancellationToken ct)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        // Client disconnect surfaces as OperationCanceledException; swallow so it
        // reports the on-wire 499 instead of an unhandled-exception 500 in logs.
        try
        {
            await foreach (var msg in outbox.Subscribe(ct))
            {
                var json = JsonSerializer.Serialize(msg, SseJsonOptions);
                await http.Response.WriteAsync($"data: {json}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public sealed class DevWhatsappLog;
}
