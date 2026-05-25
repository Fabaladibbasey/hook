using Hook.Features.ChatSession.SessionAggregate;
using Wolverine.Attributes;

namespace Hook.Features.ChatSession.SendMessage;

public sealed class AcceptChatMessageHandler(IChatRepository chats, TimeProvider clock)
{
    // [NonTransactional]: AutoApplyTransactions would BEGIN/COMMIT for every send
    // including reject paths (SessionRevoked/SessionEnded/Replay) that touch no
    // state. We open an explicit tx only on the actual insert branch so the hot
    // real-time send loop does not pay BEGIN/COMMIT on rejections.
    [NonTransactional]
    public async Task<AcceptChatMessageResponse> Handle(AcceptChatMessageCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var participant = await chats.GetParticipantAsync(cmd.ParticipantId, ct);
        if (participant is null || !participant.IsCurrentSession(cmd.SessionId))
            return new(AcceptChatMessageResult.SessionRevoked, null, now);

        var session = await chats.GetSessionAsync(cmd.ChatId, ct);
        if (session is null || !session.CanSendMessage(now))
            return new(AcceptChatMessageResult.SessionEnded, null, now);

        // Replay rejection MUST happen before TryAddMessageAsync. The Duplicate branch
        // below (PK or sequence-unique conflict) returns without advancing
        // LastInboundSequence — line ordering (advance-after-insert) is the load-bearing
        // invariant. A future refactor moving TryAdvanceSequence ahead of TryAddMessageAsync
        // would silently regress; AcceptChatMessageHandlerTests pin it.
        if (cmd.Sequence <= participant.LastInboundSequence) return new(AcceptChatMessageResult.Replay, null, now);
        if (cmd.Sequence > participant.LastInboundSequence + ChatHubConstants.MaxSequenceJump)
            return new(AcceptChatMessageResult.Replay, null, now);

        var msg = ChatMessage.Create(
            id: cmd.MessageId,
            chatId: cmd.ChatId,
            participantId: cmd.ParticipantId,
            sequence: cmd.Sequence,
            ciphertext: cmd.Ciphertext,
            nonce: cmd.Nonce,
            now: now);

        // Explicit tx ONLY on the insert path. Rollback covers both
        // 23505-as-Duplicate (savepoint-released insert needs full rollback when
        // we cannot continue) and the concurrency-token loss path where a
        // RotateSession/End raced between the pre-check and commit.
        await using var tx = await chats.BeginTransactionAsync(ct);

        if (!await chats.TryAddMessageAsync(msg, ct))
        {
            await tx.RollbackAsync(ct);
            return new(AcceptChatMessageResult.Duplicate, null, now);
        }

        participant.TryAdvanceSequence(cmd.Sequence);
        session.Touch(now);

        if (!await chats.TryCommitAsync(ct))
        {
            await tx.RollbackAsync(ct);
            return new(AcceptChatMessageResult.SessionEnded, null, now);
        }

        await tx.CommitAsync(ct);
        return new(AcceptChatMessageResult.Accepted, msg, now);
    }
}
