using Hook.Features.ChatSession.AccessLog;

namespace Hook.Features.ChatSession.OpenChat;

public sealed class RotateSessionHandler(IChatRepository chats, TimeProvider clock)
{
    public async Task<RotateSessionResponse> Handle(RotateSessionCommand cmd, CancellationToken ct)
    {
        var participant = await chats.GetByTokenAsync(cmd.Token, ct);
        if (participant is null) return new RotateSessionResponse(RotateSessionResult.NotFound, null);

        var session = await chats.GetSessionAsync(participant.ChatId, ct);
        if (session is null) return new RotateSessionResponse(RotateSessionResult.NotFound, null);

        var newSessionId = participant.RotateSession();
        await chats.AddAccessLogAsync(
            ChatAccessLog.Record(
                chatId: participant.ChatId,
                participantId: participant.Id,
                ipAddress: cmd.IpAddress,
                deviceInfo: cmd.DeviceInfo,
                now: clock.GetUtcNow()),
            ct);
        // AutoApplyTransactions flushes the RotateSession Version bump + access log
        // insert in the same commit as any post-commit envelopes.

        return new RotateSessionResponse(
            RotateSessionResult.Rotated,
            new RotateSessionData(
                ChatId: participant.ChatId,
                ParticipantId: participant.Id,
                Role: participant.Role.ToString(),
                SessionId: newSessionId,
                Status: session.Status.ToString(),
                ExpiresAt: session.ExpiresAt));
    }
}
