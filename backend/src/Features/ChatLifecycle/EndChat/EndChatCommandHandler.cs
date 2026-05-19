using Hook.Features.ChatSession;
using Hook.Features.ChatSession.SessionAggregate;

namespace Hook.Features.ChatLifecycle.EndChat;

public sealed class EndChatCommandHandler(IChatRepository chats, TimeProvider clock)
{
    public async Task<EndChatOutcome> Handle(EndChatCommand cmd, CancellationToken ct)
    {
        var session = await chats.GetSessionAsync(cmd.ChatId, ct);
        if (session is null) return new(EndChatResult.NotFound);
        if (session.Status != ChatSessionStatus.Active) return new(EndChatResult.AlreadyEnded);
        session.End(clock.GetUtcNow(), cmd.Reason, cmd.EndedBy);
        return new(EndChatResult.Ended);
    }
}
