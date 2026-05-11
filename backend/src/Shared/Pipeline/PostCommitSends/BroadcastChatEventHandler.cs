using System.Text.Json;
using Hook.Features.ChatSession;
using Microsoft.AspNetCore.SignalR;

namespace Hook.Shared.Pipeline.PostCommitSends;

public sealed class BroadcastChatEventHandler(IHubContext<ChatHub> hub)
{
    public Task Handle(BroadcastChatEventRequested evt, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<object>(evt.PayloadJson);
        return hub.Clients.Group(ChatHub.ChatGroup(evt.ChatId))
            .SendAsync(evt.EventName, payload, ct);
    }
}
