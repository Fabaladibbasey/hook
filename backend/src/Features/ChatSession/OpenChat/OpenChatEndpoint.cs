using Hook.Features.ChatSession.AccessLog;
using Hook.Shared.Persistence.Data;

namespace Hook.Features.ChatSession.OpenChat;

public static class OpenChatEndpoint
{
    public static IEndpointRouteBuilder MapChat(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/chat/open", async (
            string token,
            IChatRepository chats,
            HookDbContext db,
            HttpRequest request,
            TimeProvider clock,
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

            await chats.AddAccessLogAsync(ChatAccessLog.Record(
                chatId: participant.ChatId,
                participantId: participant.Id,
                ipAddress: ip,
                deviceInfo: ua,
                now: clock.GetUtcNow()), ct);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Chat link opened: chatId={ChatId} participantId={ParticipantId}",
                participant.ChatId, participant.Id);

            return Results.Ok(new OpenChatResponse(
                participant.ChatId,
                participant.Id,
                participant.Role.ToString(),
                newSessionId,
                session.Status.ToString(),
                session.ExpiresAt));
        });

        return routes;
    }

    public sealed class OpenChatLog;
}
