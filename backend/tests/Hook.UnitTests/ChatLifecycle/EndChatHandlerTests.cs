using Hook.Features.ChatLifecycle.EndChat;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.ParticipantAggregate;
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
    public async Task Handle_ValidParticipant_ReturnsEnded()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now.AddMinutes(-10));
        var participant = ChatParticipant.Create(session.Id, ChatParticipantRole.Client, "+2207000001");
        var handler = Build(session, out var chats);
        chats.Setup(x => x.GetParticipantAsync(participant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);

        var outcome = await handler.Handle(
            new EndChatCommand(session.Id, EndChatReason.User, "Client", participant.Id), CancellationToken.None);

        outcome.Result.ShouldBe(EndChatResult.Ended);
        session.Status.ShouldBe(ChatSessionStatus.Ended);
    }

    [Fact]
    public async Task Handle_ParticipantBelongsToOtherChat_ReturnsUnauthorized()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now.AddMinutes(-10));
        var foreignParticipant = ChatParticipant.Create(Guid.NewGuid(), ChatParticipantRole.Client, "+2207000002");
        var handler = Build(session, out var chats);
        chats.Setup(x => x.GetParticipantAsync(foreignParticipant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(foreignParticipant);

        var outcome = await handler.Handle(
            new EndChatCommand(session.Id, EndChatReason.User, "Client", foreignParticipant.Id),
            CancellationToken.None);

        outcome.Result.ShouldBe(EndChatResult.Unauthorized);
        session.Status.ShouldBe(ChatSessionStatus.Active);
    }

    [Fact]
    public async Task Handle_ParticipantMissing_ReturnsUnauthorized()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now.AddMinutes(-10));
        var handler = Build(session, out var chats);
        chats.Setup(x => x.GetParticipantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatParticipant?)null);

        var outcome = await handler.Handle(
            new EndChatCommand(session.Id, EndChatReason.User, "Client", Guid.NewGuid()),
            CancellationToken.None);

        outcome.Result.ShouldBe(EndChatResult.Unauthorized);
        session.Status.ShouldBe(ChatSessionStatus.Active);
    }

    [Fact]
    public async Task Handle_NullParticipantId_AppliesSystemEnd()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now.AddMinutes(-10));
        var handler = Build(session, out var chats);

        var outcome = await handler.Handle(
            new EndChatCommand(session.Id, EndChatReason.Idle, string.Empty), CancellationToken.None);

        outcome.Result.ShouldBe(EndChatResult.Ended);
        session.Status.ShouldBe(ChatSessionStatus.Ended);
        chats.Verify(x => x.GetParticipantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
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
