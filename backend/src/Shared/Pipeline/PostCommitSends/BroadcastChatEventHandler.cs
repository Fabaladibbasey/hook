using Hook.Features.ChatSession;
using Microsoft.AspNetCore.SignalR;
using Wolverine.Attributes;

namespace Hook.Shared.Pipeline.PostCommitSends;

public sealed class BroadcastChatEventHandler(IHubContext<ChatHub> hub)
{
    [NonTransactional]
    public Task Handle(BroadcastChatEventRequested evt, CancellationToken ct) =>
        hub.Clients.Group(ChatHub.ChatGroup(evt.ChatId))
            .SendAsync(evt.EventName, evt.Payload, ct);
}
