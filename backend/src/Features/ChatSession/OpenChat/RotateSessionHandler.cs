using Hook.Features.ChatSession.AccessLog;
using Hook.Features.ChatSession.SessionAggregate;

namespace Hook.Features.ChatSession.OpenChat;

public sealed class RotateSessionHandler(IChatRepository chats, TimeProvider clock)
{
    public async Task<RotateSessionResponse> Handle(RotateSessionCommand cmd, CancellationToken ct)
    {
        var participant = await chats.GetByTokenAsync(cmd.Token, ct);
        if (participant is null) return new RotateSessionResponse(RotateSessionResult.NotFound, null);

        var session = await chats.GetSessionAsync(participant.ChatId, ct);
        if (session is null) return new RotateSessionResponse(RotateSessionResult.NotFound, null);
        if (session.Status != ChatSessionStatus.Active)
            return new RotateSessionResponse(RotateSessionResult.NotFound, null);

        var newSessionId = participant.RotateSession();

        await chats.AddAccessLogAsync(
            ChatAccessLog.Record(
                chatId: participant.ChatId,
                participantId: participant.Id,
                ipAddress: cmd.IpAddress,
                deviceInfo: cmd.DeviceInfo,
                now: clock.GetUtcNow()),
            ct);

        // Flush now so a concurrent rotate landing between GetByTokenAsync and commit
        // surfaces as Conflict (retriable) rather than DbUpdateConcurrencyException
        // bubbling through AutoApplyTransactions as 500.
        if (!await chats.TryCommitAsync(ct))
            return new RotateSessionResponse(RotateSessionResult.Conflict, null);

        return new RotateSessionResponse(
            RotateSessionResult.Rotated,
            new OpenChatResponse(
                ChatId: participant.ChatId,
                ParticipantId: participant.Id,
                Role: participant.Role.ToString(),
                SessionId: newSessionId,
                Status: session.Status.ToString(),
                ExpiresAt: session.ExpiresAt,
                OutboundSequenceCursor: participant.LastInboundSequence));
    }
}
