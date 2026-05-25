using Hook.Features.ChatSession.AccessLog;

namespace Hook.Features.ChatSession.OpenChat;

public sealed class RotateSessionHandler(IChatRepository chats, TimeProvider clock)
{
    public async Task<RotateSessionResponse> Handle(RotateSessionCommand cmd, CancellationToken ct)
    {
        var participant = await chats.GetByTokenAsync(cmd.Token, ct);
        if (participant is null) return Empty(RotateSessionResult.NotFound);

        var session = await chats.GetSessionAsync(participant.ChatId, ct);
        if (session is null) return Empty(RotateSessionResult.NotFound);

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
            Result: RotateSessionResult.Rotated,
            ChatId: participant.ChatId,
            ParticipantId: participant.Id,
            Role: participant.Role.ToString(),
            SessionId: newSessionId,
            Status: session.Status.ToString(),
            ExpiresAt: session.ExpiresAt);
    }

    private static RotateSessionResponse Empty(RotateSessionResult result) =>
        new(result, Guid.Empty, Guid.Empty, string.Empty, Guid.Empty, string.Empty, default);
}
