using Wolverine;

namespace Hook.Features.ChatSession.OpenChat;

public static class OpenChatEndpoint
{
    // Cap UA at a safe length + strip control chars before persistence (log-injection
    // hygiene). RemoteIpAddress is populated by UseForwardedHeaders (private bridge CIDR
    // in production) so we get the real client IP, not the reverse-proxy's loopback.
    private const int MaxDeviceInfoLength = 200;

    public static IEndpointRouteBuilder MapChat(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/chat/open", async (
            string token,
            IMessageBus bus,
            HttpRequest request,
            ILogger<OpenChatLog> logger,
            CancellationToken ct) =>
        {
            var ip = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
            var ua = SanitizeDeviceInfo(request.Headers.UserAgent.ToString());

            var response = await bus.InvokeAsync<RotateSessionResponse>(
                new RotateSessionCommand(token, ip, ua), ct);

            if (response.Result == RotateSessionResult.NotFound) return Results.NotFound();

            logger.LogInformation("Chat link opened: chatId={ChatId} participantId={ParticipantId}",
                response.ChatId, response.ParticipantId);

            return Results.Ok(new OpenChatResponse(
                response.ChatId,
                response.ParticipantId,
                response.Role,
                response.SessionId,
                response.Status,
                response.ExpiresAt));
        });

        return routes;
    }

    internal static string SanitizeDeviceInfo(string ua)
    {
        if (string.IsNullOrEmpty(ua)) return string.Empty;
        var clean = new string(ua.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length > MaxDeviceInfoLength ? clean[..MaxDeviceInfoLength] : clean;
    }

    public sealed class OpenChatLog;
}
