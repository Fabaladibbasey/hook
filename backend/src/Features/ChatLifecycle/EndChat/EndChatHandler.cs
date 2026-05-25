using Hook.Features.ChatSession;
using Hook.Features.ChatSession.SessionAggregate;

namespace Hook.Features.ChatLifecycle.EndChat;

public sealed class EndChatHandler(IChatRepository chats, TimeProvider clock)
{
    public async Task<EndChatResponse> Handle(EndChatCommand cmd, CancellationToken ct)
    {
        // Null ParticipantId = system-initiated (Idle/Expired/ProductiveSilence); skip the bind check.
        // Non-null = user-initiated; the caller must prove the participant belongs to this chat.
        if (cmd.ParticipantId is { } pid)
        {
            var participant = await chats.GetParticipantAsync(pid, ct);
            if (participant is null || participant.ChatId != cmd.ChatId)
                return new(EndChatResult.Unauthorized);
        }

        var session = await chats.GetSessionAsync(cmd.ChatId, ct);
        if (session is null) return new(EndChatResult.NotFound);
        if (session.Status != ChatSessionStatus.Active) return new(EndChatResult.AlreadyEnded);
        session.End(clock.GetUtcNow(), cmd.Reason, cmd.EndedBy);
        return new(EndChatResult.Ended);
    }
}
