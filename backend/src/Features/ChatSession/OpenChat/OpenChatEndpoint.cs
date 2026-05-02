using Hook.Features.ChatSession.AccessLog;

namespace Hook.Features.ChatSession.OpenChat;

public static class OpenChatEndpoint
{
    public static IEndpointRouteBuilder MapChat(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/chat/open", async (
            string token,
            IChatRepository chats,
            HttpRequest request,
            ILogger<OpenChatLog> logger,
            CancellationToken ct) =>
        {
            var participant = await chats.GetByTokenAsync(token, ct);
            if (participant is null) return Results.NotFound();

            var session = await chats.GetSessionAsync(participant.ChatId, ct);
            if (session is null) return Results.NotFound();

            var newSessionId = participant.RotateSession();
            var ip = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
            var ua = request.Headers.UserAgent.ToString();

            await chats.AddAccessLogAsync(new ChatAccessLog
            {
                ChatId = participant.ChatId,
                ParticipantId = participant.Id,
                IpAddress = ip,
                DeviceInfo = ua
            }, ct);
            await chats.SaveChangesAsync(ct);

            logger.LogInformation("Chat link opened: chatId={ChatId} participantId={ParticipantId}",
                participant.ChatId, participant.Id);

            return Results.Ok(new
            {
                chatId = participant.ChatId,
                participantId = participant.Id,
                role = participant.Role.ToString(),
                sessionId = newSessionId,
                status = session.Status.ToString(),
                expiresAt = session.ExpiresAt
            });
        });

        return routes;
    }

    public sealed class OpenChatLog;
}
