using Hook.Features.ChatLifecycle.EndChat;
using Hook.Features.ChatLifecycle.Events;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Hook.UnitTests.ChatLifecycle;

public class IdleEndHandlerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-05-09T12:00:00Z");

    private static (IdleEndHandler handler, Mock<IChatRepository> chats, ChatSession session) Build(
        ChatSession session)
    {
        var chats = new Mock<IChatRepository>();
        chats.Setup(x => x.GetSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var clock = new FakeTimeProvider(Now);
        var handler = new IdleEndHandler(chats.Object, clock, NullLogger<IdleEndHandler>.Instance);
        return (handler, chats, session);
    }

    [Fact]
    public async Task Handle_IdleSession_EndsItAndStagesBroadcastChatEventRequested()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now.AddMinutes(-10));
        var (handler, _, _) = Build(session);

        await handler.Handle(new IdleEndCheck(session.Id, session.LastActivityAt), CancellationToken.None);

        session.Status.ShouldBe(ChatSessionStatus.Ended);
        var evt = session.DequeueEvents().Single().ShouldBeOfType<BroadcastChatEventRequested>();
        evt.ChatId.ShouldBe(session.Id);
        evt.EventName.ShouldBe(ChatHubEvents.ChatEnded);
        var payload = evt.Payload.ShouldBeOfType<ChatEndedPayload>();
        payload.Reason.ShouldBe("idle");
        payload.EndedBy.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_SessionMissing_NoOp()
    {
        var chats = new Mock<IChatRepository>();
        chats.Setup(x => x.GetSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatSession?)null);
        var handler = new IdleEndHandler(chats.Object, new FakeTimeProvider(Now),
            NullLogger<IdleEndHandler>.Instance);

        await handler.Handle(new IdleEndCheck(Guid.NewGuid(), Now), CancellationToken.None);
        // No throw; nothing else to assert.
    }

    [Fact]
    public async Task Handle_FresherActivity_SkipsEnd()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now.AddMinutes(-10));
        session.Touch(Now); // bumps LastActivityAt to Now
        var (handler, _, _) = Build(session);

        await handler.Handle(new IdleEndCheck(session.Id, Now.AddMinutes(-5)), CancellationToken.None);

        session.Status.ShouldBe(ChatSessionStatus.Active);
        session.DequeueEvents().Count.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_AlreadyEndedSession_SkipsEndAndStagesNoEvent()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now.AddMinutes(-10));
        session.End(Now.AddMinutes(-1), "user", "Client");
        // Drain the event raised by the prior End() so we observe only what the handler does.
        session.DequeueEvents();
        var (handler, _, _) = Build(session);

        await handler.Handle(new IdleEndCheck(session.Id, session.LastActivityAt), CancellationToken.None);

        session.DequeueEvents().Count.ShouldBe(0);
    }
}
