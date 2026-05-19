using Hook.Features.ChatLifecycle.EndChat;
using Hook.Features.ChatLifecycle.Events;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.SessionAggregate;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.ChatLifecycle;

public class IdleEndHandlerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static (IdleEndHandler handler, Mock<IMessageBus> bus, ChatSession session) Build(
        ChatSession session)
    {
        var chats = new Mock<IChatRepository>();
        chats.Setup(x => x.GetSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var bus = new Mock<IMessageBus>();
        var handler = new IdleEndHandler(chats.Object, NullLogger<IdleEndHandler>.Instance);
        return (handler, bus, session);
    }

    [Fact]
    public async Task Handle_IdleSession_PublishesEndChatCommand()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now.AddMinutes(-10));
        var (handler, bus, _) = Build(session);

        await handler.Handle(new IdleEndCheck(session.Id, session.LastActivityAt), bus.Object, CancellationToken.None);

        bus.Verify(b => b.PublishAsync(
            It.Is<EndChatCommand>(c => c.ChatId == session.Id && c.Reason == EndChatReason.Idle && c.EndedBy == null),
            It.IsAny<DeliveryOptions>()), Times.Once);
        session.Status.ShouldBe(ChatSessionStatus.Active); // handler owns the mutation now
    }

    [Fact]
    public async Task Handle_SessionMissing_NoOp()
    {
        var chats = new Mock<IChatRepository>();
        chats.Setup(x => x.GetSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatSession?)null);
        var bus = new Mock<IMessageBus>(MockBehavior.Strict);
        var handler = new IdleEndHandler(chats.Object, NullLogger<IdleEndHandler>.Instance);

        await handler.Handle(new IdleEndCheck(Guid.NewGuid(), Now), bus.Object, CancellationToken.None);
        // Strict mock asserts no bus calls.
    }

    [Fact]
    public async Task Handle_FresherActivity_SkipsPublish()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now.AddMinutes(-10));
        session.Touch(Now);
        var (handler, bus, _) = Build(session);

        await handler.Handle(new IdleEndCheck(session.Id, Now.AddMinutes(-5)), bus.Object, CancellationToken.None);

        bus.Verify(b => b.PublishAsync(It.IsAny<EndChatCommand>(), It.IsAny<DeliveryOptions>()), Times.Never);
        session.Status.ShouldBe(ChatSessionStatus.Active);
    }

    [Fact]
    public async Task Handle_AlreadyEndedSession_SkipsPublish()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now.AddMinutes(-10));
        session.End(Now.AddMinutes(-1), EndChatReason.User, "Client");
        session.DequeueEvents();
        var (handler, bus, _) = Build(session);

        await handler.Handle(new IdleEndCheck(session.Id, session.LastActivityAt), bus.Object, CancellationToken.None);

        bus.Verify(b => b.PublishAsync(It.IsAny<EndChatCommand>(), It.IsAny<DeliveryOptions>()), Times.Never);
    }
}
