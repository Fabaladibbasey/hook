using Hook.Features.ChatLifecycle.EndChat;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Shared.Pipeline.PostCommitSends;
using Shouldly;

namespace Hook.UnitTests.Chat;

public class ChatSessionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void End_Idle_RaisesBroadcastChatEvent_WithIdlePayload()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now);

        session.End(Now, EndChatReason.Idle);

        var events = session.DequeueEvents();
        events.Count.ShouldBe(1);
        var evt = events[0].ShouldBeOfType<BroadcastChatEvent>();
        evt.ChatId.ShouldBe(session.Id);
        evt.EventName.ShouldBe(ChatHubEvents.ChatEnded);
        var payload = evt.Payload.ShouldBeOfType<ChatEndedPayload>();
        payload.Reason.ShouldBe("idle");
        payload.EndedBy.ShouldBeEmpty();
    }

    [Fact]
    public void End_User_RaisesBroadcastChatEvent_WithEndedBy()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now);

        session.End(Now, EndChatReason.User, "Client");

        var evt = session.DequeueEvents().Single().ShouldBeOfType<BroadcastChatEvent>();
        var payload = evt.Payload.ShouldBeOfType<ChatEndedPayload>();
        payload.Reason.ShouldBe("user");
        payload.EndedBy.ShouldBe("Client");
    }

    [Fact]
    public void DequeueEvents_IsIdempotent()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now);
        session.End(Now, EndChatReason.Idle);

        session.DequeueEvents().Count.ShouldBe(1);
        session.DequeueEvents().Count.ShouldBe(0);
    }

    [Fact]
    public void End_TransitionsToEnded_AndExpiresImmediately()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now);

        session.End(Now, EndChatReason.Idle);

        session.Status.ShouldBe(ChatSessionStatus.Ended);
        session.ExpiresAt.ShouldBe(Now);
        session.CanSendMessage(Now).ShouldBeFalse();
    }

    [Fact]
    public void HardExpire_WhenAlreadyEnded_Throws()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now);
        session.End(Now, EndChatReason.Idle);

        Should.Throw<InvalidOperationException>(() => session.HardExpire(Now))
            .Message.ShouldContain("cannot be hard-expired");
    }
}
