using Hook.Features.ChatLifecycle.EndChat;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Hook.UnitTests.ChatLifecycle;

public class EndChatHandlerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static EndChatHandler Build(ChatSession? session, out Mock<IChatRepository> chats)
    {
        chats = new Mock<IChatRepository>();
        chats.Setup(x => x.GetSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        return new EndChatHandler(chats.Object, new FakeTimeProvider(Now));
    }

    [Fact]
    public async Task Handle_ActiveSession_EndsAndStagesBroadcastEvent()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now.AddMinutes(-10));
        var handler = Build(session, out _);

        var outcome = await handler.Handle(
            new EndChatCommand(session.Id, EndChatReason.User, "Client"), CancellationToken.None);

        outcome.Result.ShouldBe(EndChatResult.Ended);
        session.Status.ShouldBe(ChatSessionStatus.Ended);
        var events = session.DequeueEvents();
        var broadcast = events.OfType<BroadcastChatEvent>().Single();
        broadcast.ChatId.ShouldBe(session.Id);
        broadcast.EventName.ShouldBe(ChatHubEvents.ChatEnded);
        var payload = broadcast.Payload.ShouldBeOfType<ChatEndedPayload>();
        payload.Reason.ShouldBe("user");
        payload.EndedBy.ShouldBe("Client");
        events.OfType<ChatSessionEndedEvent>().Single().Reason.ShouldBe(ChatEndReason.User);
    }

    [Fact]
    public async Task Handle_SessionMissing_ReturnsNotFound()
    {
        var handler = Build(null, out _);

        var outcome = await handler.Handle(
            new EndChatCommand(Guid.NewGuid(), EndChatReason.User, string.Empty), CancellationToken.None);

        outcome.Result.ShouldBe(EndChatResult.NotFound);
    }

    [Fact]
    public async Task Handle_AlreadyEnded_ReturnsAlreadyEndedAndDoesNotRaise()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now.AddMinutes(-10));
        session.End(Now.AddMinutes(-1), EndChatReason.User, "Client");
        session.DequeueEvents(); // drain prior event
        var handler = Build(session, out _);

        var outcome = await handler.Handle(
            new EndChatCommand(session.Id, EndChatReason.Idle, string.Empty), CancellationToken.None);

        outcome.Result.ShouldBe(EndChatResult.AlreadyEnded);
        session.DequeueEvents().ShouldBeEmpty();
    }
}
