using Hook.Features.ChatSession.ParticipantAggregate;
using Shouldly;

namespace Hook.UnitTests.Chat;

public class ChatParticipantTests
{
    private static ChatParticipant New() =>
        ChatParticipant.Create(Guid.NewGuid(), ChatParticipantRole.Client, "+22070000100");

    [Fact]
    public void TryAdvanceSequence_AcceptsStrictlyGreaterValue()
    {
        var p = New();
        p.TryAdvanceSequence(10).ShouldBeTrue();
        p.LastInboundSequence.ShouldBe(10);
    }

    [Fact]
    public void TryAdvanceSequence_RejectsEqualValue()
    {
        var p = New();
        p.TryAdvanceSequence(10).ShouldBeTrue();
        p.TryAdvanceSequence(10).ShouldBeFalse();
        p.LastInboundSequence.ShouldBe(10);
    }

    [Fact]
    public void TryAdvanceSequence_RejectsLowerValue()
    {
        var p = New();
        p.TryAdvanceSequence(20).ShouldBeTrue();
        p.TryAdvanceSequence(15).ShouldBeFalse();
        p.LastInboundSequence.ShouldBe(20);
    }

    [Fact]
    public void TwoParticipants_HaveIndependentSequences()
    {
        var a = New();
        var b = New();
        a.TryAdvanceSequence(100).ShouldBeTrue();
        b.TryAdvanceSequence(50).ShouldBeTrue();
        a.LastInboundSequence.ShouldBe(100);
        b.LastInboundSequence.ShouldBe(50);
    }

    [Fact]
    public void SetPublicKey_StoresBytesVerbatim()
    {
        var p = New();
        p.SetPublicKey([0xAA, 0xBB]);
        p.PublicKey.ShouldBe([0xAA, 0xBB]);
    }

    [Fact]
    public void SetPublicKey_OverwritesPriorKey()
    {
        var p = New();
        p.SetPublicKey([0x01]);
        p.SetPublicKey([0x02, 0x03]);
        p.PublicKey.ShouldBe([0x02, 0x03]);
    }

    [Fact]
    public void RotateSession_ClearsPublicKey_AndPreservesSequenceHighWaterMark()
    {
        var p = New();
        p.SetPublicKey([0xAA]);
        p.TryAdvanceSequence(50).ShouldBeTrue();

        var prior = p.CurrentSessionId;
        var next = p.RotateSession();

        next.ShouldNotBe(prior);
        p.PublicKey.ShouldBeNull();
        // LastInboundSequence MUST survive rotation — it backs the participant-scoped
        // unique index (ChatId, ParticipantId, Sequence) which is NOT session-scoped.
        // Resetting it would let the rotated client re-send seq=1 and collide with
        // the pre-rotation row, surfacing as Duplicate.
        p.LastInboundSequence.ShouldBe(50);
        p.IsCurrentSession(next).ShouldBeTrue();
        p.IsCurrentSession(prior).ShouldBeFalse();
    }

    [Fact]
    public void RotateSession_AloneDoesNotMutateLastInboundSequence()
    {
        var p = New();
        p.TryAdvanceSequence(42).ShouldBeTrue();
        var prior = p.LastInboundSequence;

        p.RotateSession();

        p.LastInboundSequence.ShouldBe(prior);
    }
}
