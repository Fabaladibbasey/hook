using Hook.Features.ChatSession;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace Hook.UnitTests.Shared.Pipeline;

public class BroadcastChatEventHandlerTests
{
    [Fact]
    public async Task Handle_ForwardsTypedPayloadToChatGroup_WithoutReserialization()
    {
        var chatId = Guid.NewGuid();
        var hubMock = new Mock<IHubContext<ChatHub>>();
        var clientsMock = new Mock<IHubClients>();
        var groupMock = new Mock<IClientProxy>();
        hubMock.SetupGet(h => h.Clients).Returns(clientsMock.Object);
        clientsMock.Setup(c => c.Group(ChatHub.ChatGroup(chatId))).Returns(groupMock.Object);

        IChatEventPayload payload = new ChatEndedPayload("idle");
        var evt = new BroadcastChatEvent(chatId, ChatHubEvents.ChatEnded, payload);

        await new BroadcastChatEventHandler(hubMock.Object).Handle(evt, CancellationToken.None);

        groupMock.Verify(g => g.SendCoreAsync(
            ChatHubEvents.ChatEnded,
            It.Is<object?[]>(args => args.Length == 1 && ReferenceEquals(args[0], payload)),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
