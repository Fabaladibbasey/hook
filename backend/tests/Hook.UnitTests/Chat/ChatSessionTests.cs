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

        // End raises two domain events: BroadcastChatEvent (SignalR pipeline) and
        // ChatSessionEndedEvent (Feedback slice subscriber).
        var events = session.DequeueEvents();
        events.Count.ShouldBe(2);
        var broadcast = events.OfType<BroadcastChatEvent>().Single();
        broadcast.ChatId.ShouldBe(session.Id);
        broadcast.EventName.ShouldBe(ChatHubEvents.ChatEnded);
        var payload = broadcast.Payload.ShouldBeOfType<ChatEndedPayload>();
        payload.Reason.ShouldBe("idle");
        payload.EndedBy.ShouldBeEmpty();
        var sessionEnded = events.OfType<ChatSessionEndedEvent>().Single();
        sessionEnded.ChatId.ShouldBe(session.Id);
        sessionEnded.Reason.ShouldBe(ChatEndReason.Idle);
    }

    [Fact]
    public void End_User_RaisesBroadcastChatEvent_WithEndedBy()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now);

        session.End(Now, EndChatReason.User, "Client");

        var events = session.DequeueEvents();
        events.Count.ShouldBe(2);
        var broadcast = events.OfType<BroadcastChatEvent>().Single();
        var payload = broadcast.Payload.ShouldBeOfType<ChatEndedPayload>();
        payload.Reason.ShouldBe("user");
        payload.EndedBy.ShouldBe("Client");
        events.OfType<ChatSessionEndedEvent>().Single().Reason.ShouldBe(ChatEndReason.User);
    }

    [Fact]
    public void DequeueEvents_IsIdempotent()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now);
        session.End(Now, EndChatReason.Idle);

        session.DequeueEvents().Count.ShouldBe(2);
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

    [Fact]
    public void HardExpire_FromActive_SetsExpiredAndRaisesEvents()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now);

        session.HardExpire(Now);

        session.Status.ShouldBe(ChatSessionStatus.Expired);
        session.ExpiresAt.ShouldBe(Now);
        session.CanSendMessage(Now).ShouldBeFalse();

        var events = session.DequeueEvents();
        events.Count.ShouldBe(2);
        var broadcast = events.OfType<BroadcastChatEvent>().Single();
        broadcast.ChatId.ShouldBe(session.Id);
        broadcast.EventName.ShouldBe(ChatHubEvents.ChatExpired);
        broadcast.Payload.ShouldBeOfType<ChatExpiredPayload>();
        events.OfType<ChatSessionEndedEvent>().Single().Reason.ShouldBe(ChatEndReason.Expired);
    }

    [Fact]
    public void End_TwiceFromActive_OnlyRaisesEventsOnce()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now);

        session.End(Now, EndChatReason.User, "Client");
        var firstBatch = session.DequeueEvents();
        firstBatch.Count.ShouldBe(2);

        // Second call must no-op: aggregate is already Ended.
        session.End(Now, EndChatReason.Idle);
        session.DequeueEvents().Count.ShouldBe(0);
    }

    [Fact]
    public void End_WithUnmappedReason_ThrowsBeforeMutation()
    {
        var session = ChatSession.Create(TimeSpan.FromMinutes(30), Now);

        Should.Throw<ArgumentOutOfRangeException>(() =>
            session.End(Now, EndChatReason.AlreadyEnded));

        // MapEndReason validates upfront, so Status stayed Active and no events queued.
        session.Status.ShouldBe(ChatSessionStatus.Active);
        session.DequeueEvents().Count.ShouldBe(0);
    }
}
