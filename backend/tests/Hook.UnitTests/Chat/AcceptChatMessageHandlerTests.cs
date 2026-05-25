using Hook.Features.ChatLifecycle.EndChat;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.AccessLog;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SendMessage;
using Hook.Features.ChatSession.SessionAggregate;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Hook.UnitTests.Chat;

public class AcceptChatMessageHandlerTests
{
    private readonly Mock<IChatRepository> _chats = new();
    private readonly Mock<IDbContextTransaction> _tx = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    private readonly ChatSession _session;
    private readonly ChatParticipant _participant;

    private bool _txCommitted;

    public AcceptChatMessageHandlerTests()
    {
        _session = ChatSession.Create(TimeSpan.FromMinutes(30), _clock.GetUtcNow());
        _participant = ChatParticipant.Create(_session.Id, ChatParticipantRole.Client, "+2203339999");

        _chats.Setup(x => x.GetParticipantAsync(_participant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_participant);
        _chats.Setup(x => x.GetSessionAsync(_session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_session);
        _chats.Setup(x => x.TryAddMessageAsync(It.IsAny<ChatMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _chats.Setup(x => x.TryCommitAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _chats.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_tx.Object);

        _tx.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _txCommitted = true).Returns(Task.CompletedTask);
    }

    private AcceptChatMessageHandler Build() => new(_chats.Object, _clock);

    private AcceptChatMessageCommand BuildCmd(long sequence = 1, Guid? messageId = null) =>
        new(SessionId: _participant.CurrentSessionId,
            ChatId: _session.Id,
            ParticipantId: _participant.Id,
            MessageId: messageId ?? Guid.NewGuid(),
            Sequence: sequence,
            Ciphertext: new byte[17],
            Nonce: new byte[12]);

    [Fact]
    public async Task ParticipantMissing_ReturnsSessionRevoked()
    {
        _chats.Setup(x => x.GetParticipantAsync(_participant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatParticipant?)null);

        var resp = await Build().Handle(BuildCmd(), CancellationToken.None);

        resp.Result.ShouldBe(AcceptChatMessageResult.SessionRevoked);
        _txCommitted.ShouldBeFalse();
    }

    [Fact]
    public async Task WrongSession_ReturnsSessionRevoked()
    {
        var cmd = BuildCmd() with { SessionId = Guid.NewGuid() };

        var resp = await Build().Handle(cmd, CancellationToken.None);

        resp.Result.ShouldBe(AcceptChatMessageResult.SessionRevoked);
    }

    [Fact]
    public async Task SessionEnded_ReturnsSessionEnded()
    {
        _session.End(_clock.GetUtcNow(), EndChatReason.User);

        var resp = await Build().Handle(BuildCmd(), CancellationToken.None);

        resp.Result.ShouldBe(AcceptChatMessageResult.SessionEnded);
    }

    [Fact]
    public async Task SessionExpired_ReturnsSessionEnded()
    {
        // Push clock past ExpiresAt so CanSendMessage returns false even on Active.
        _clock.Advance(TimeSpan.FromHours(1));

        var resp = await Build().Handle(BuildCmd(), CancellationToken.None);

        resp.Result.ShouldBe(AcceptChatMessageResult.SessionEnded);
    }

    [Fact]
    public async Task SequenceLeqLastInbound_ReturnsReplay()
    {
        _participant.TryAdvanceSequence(5);

        var resp = await Build().Handle(BuildCmd(sequence: 5), CancellationToken.None);

        resp.Result.ShouldBe(AcceptChatMessageResult.Replay);
        _participant.LastInboundSequence.ShouldBe(5);
    }

    [Fact]
    public async Task SequenceTooFarAhead_ReturnsReplay()
    {
        // Cap incoming jump at LastInbound + MaxSequenceJump. A long.MaxValue jump
        // would otherwise self-DoS the participant.
        var resp = await Build().Handle(BuildCmd(sequence: long.MaxValue), CancellationToken.None);

        resp.Result.ShouldBe(AcceptChatMessageResult.Replay);
    }

    [Fact]
    public async Task SequenceExactlyAtCap_ReturnsAccepted()
    {
        // Pin the `>` boundary in AcceptChatMessageHandler: LastInbound + MaxJump is
        // the highest accepted sequence; off-by-one regressions flip this to Replay.
        var resp = await Build().Handle(BuildCmd(sequence: ChatHubConstants.MaxSequenceJump), CancellationToken.None);

        resp.Result.ShouldBe(AcceptChatMessageResult.Accepted);
    }

    [Fact]
    public async Task SequenceJustOverCap_ReturnsReplay()
    {
        var resp = await Build().Handle(
            BuildCmd(sequence: ChatHubConstants.MaxSequenceJump + 1),
            CancellationToken.None);

        resp.Result.ShouldBe(AcceptChatMessageResult.Replay);
    }

    [Fact]
    public async Task DuplicateMessageId_ReturnsDuplicate_AndNoSequenceAdvance()
    {
        // Pin advance-after-insert invariant: when TryAddMessageAsync hits 23505,
        // LastInboundSequence MUST stay at its prior value.
        _chats.Setup(x => x.TryAddMessageAsync(It.IsAny<ChatMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var resp = await Build().Handle(BuildCmd(sequence: 7), CancellationToken.None);

        resp.Result.ShouldBe(AcceptChatMessageResult.Duplicate);
        _participant.LastInboundSequence.ShouldBe(0);
        // `await using var tx` rolls back on dispose when CommitAsync is not called —
        // assert the invariant by proving no commit reached the tx.
        _txCommitted.ShouldBeFalse();
    }

    [Fact]
    public async Task Accepted_AdvancesSequenceAndTouchesSession_AndCommitsTx()
    {
        var beforeTouch = _session.LastActivityAt;
        _clock.Advance(TimeSpan.FromSeconds(5));

        var resp = await Build().Handle(BuildCmd(sequence: 3), CancellationToken.None);

        resp.Result.ShouldBe(AcceptChatMessageResult.Accepted);
        _participant.LastInboundSequence.ShouldBe(3);
        _session.LastActivityAt.ShouldBeGreaterThan(beforeTouch);
        _txCommitted.ShouldBeTrue();
    }

    [Fact]
    public async Task EndBetweenCheckAndCommit_ReturnsSessionEnded_AndDoesNotCommit()
    {
        // TryCommitAsync returns false → simulates a concurrent End/HardExpire bumping
        // ChatSession.Version between this scope's load and commit. `await using`
        // disposes the unconmitted tx, rolling back the insert savepoint.
        _chats.Setup(x => x.TryCommitAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var resp = await Build().Handle(BuildCmd(), CancellationToken.None);

        resp.Result.ShouldBe(AcceptChatMessageResult.SessionEnded);
        _txCommitted.ShouldBeFalse();
    }

    [Fact]
    public async Task AfterAdvanceTo5_Send6_Accepted_And_Send5_RejectedAsReplay()
    {
        // Legacy participant carrying LastInboundSequence > 0 across a rotation:
        // a fresh send at cursor + 1 must still be accepted; replay at the cursor
        // value must still be rejected.
        _participant.TryAdvanceSequence(5).ShouldBeTrue();

        var accepted = await Build().Handle(BuildCmd(sequence: 6), CancellationToken.None);
        accepted.Result.ShouldBe(AcceptChatMessageResult.Accepted);

        var replayed = await Build().Handle(BuildCmd(sequence: 5), CancellationToken.None);
        replayed.Result.ShouldBe(AcceptChatMessageResult.Replay);
    }

    [Fact]
    public async Task NoTransactionOpened_OnRejectPaths()
    {
        // SessionRevoked/SessionEnded/Replay must not pay BEGIN/COMMIT cost.
        var cmd = BuildCmd() with { SessionId = Guid.NewGuid() };

        await Build().Handle(cmd, CancellationToken.None);

        _chats.Verify(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
