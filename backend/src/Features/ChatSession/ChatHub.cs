using Hook.Features.ChatLifecycle;
using Hook.Features.ChatLifecycle.EndChat;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.PublishKey;
using Hook.Features.ChatSession.SendMessage;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.AspNetCore.SignalR;
using Wolverine;

namespace Hook.Features.ChatSession;

public sealed class ChatHub(
    IChatRepository chats,
    ChatScheduler scheduler,
    IMessageBus bus,
    ChatHubMessageLimiter limiter,
    ILogger<ChatHub> logger,
    TimeProvider clock) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var token = http?.Request.Query["token"].ToString();
        var sessionIdRaw = http?.Request.Query["sessionId"].ToString();
        if (string.IsNullOrEmpty(token)
            || !Guid.TryParse(sessionIdRaw, out var sessionId)
            || sessionId == Guid.Empty)
        {
            Context.Abort();
            return;
        }

        var participant = await chats.GetByTokenAsync(token);
        if (participant is null) { Context.Abort(); return; }

        Context.Items[ChatHubConstants.Items.ChatId] = participant.ChatId;
        Context.Items[ChatHubConstants.Items.ParticipantId] = participant.Id;
        Context.Items[ChatHubConstants.Items.SessionId] = sessionId;
        Context.Items[ChatHubConstants.Items.Role] = participant.Role.ToString();

        // Long-polling can drop a SendAsync queued during OnConnectedAsync, so revocation/end is
        // surfaced on the next hub invocation (matches "revoking the prior tab on its next hub
        // interaction" in CLAUDE.md). Skip group join + HistoryLoaded for a stale session so the
        // tab sees nothing until it triggers the explicit reject.
        if (!participant.IsCurrentSession(sessionId)) return;

        var session = await chats.GetSessionAsync(participant.ChatId);
        if (session is null || !session.CanSendMessage(clock.GetUtcNow())) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, ChatGroup(participant.ChatId));

        var history = await chats.GetMessagesAsync(participant.ChatId, ChatHubConstants.InitialHistoryTake);
        await Clients.Caller.SendAsync(ChatHubConstants.Events.HistoryLoaded, history.Select(ToWire));

        await base.OnConnectedAsync();
    }

    public async Task PublishKey(string publicKeyB64)
    {
        if (!TryDecodeBytes(publicKeyB64, minLen: 1, maxLen: ChatHubConstants.MaxPublicKeyBytes, out var spki))
        {
            Context.Abort();
            return;
        }

        if (Context.Items[ChatHubConstants.Items.ChatId] is not Guid chatId
            || Context.Items[ChatHubConstants.Items.ParticipantId] is not Guid participantId
            || Context.Items[ChatHubConstants.Items.SessionId] is not Guid sessionId)
        {
            Context.Abort();
            return;
        }

        // No client-facing PublishKey-rejected payload exists; a tight publish loop
        // is hostile, drop the connection.
        if (!limiter.TryAcquire(participantId.ToString()).IsAllowed)
        {
            Context.Abort();
            return;
        }

        var response = await bus.InvokeAsync<PublishParticipantKeyResponse>(
            new PublishParticipantKeyCommand(sessionId, chatId, participantId, spki));

        if (response.Result is PublishParticipantKeyResult.SessionRevoked
                              or PublishParticipantKeyResult.ParticipantMissing)
        {
            await Clients.Caller.SendAsync(ChatHubConstants.Events.SessionRevoked);
            Context.Abort();
            return;
        }

        if (response.Result == PublishParticipantKeyResult.InvalidKey)
        {
            logger.LogWarning(
                "Invalid SPKI rejected chat={ChatId} participant={ParticipantId}", chatId, participantId);
            Context.Abort();
            return;
        }

        // Caller-side notification stays inline — the response payload carries the
        // PEER's key for this caller. Broadcast to other group members is owned by
        // the outbox-driven BroadcastChatEvent raised by ChatParticipant.SetPublicKey.
        if (response.PeerPublicKey.Length > 0)
        {
            await Clients.Caller.SendAsync(ChatHubConstants.Events.PeerKeyAvailable, new
            {
                peerParticipantId = response.PeerParticipantId,
                peerPublicKeyB64 = Convert.ToBase64String(response.PeerPublicKey)
            });
        }

        logger.LogDebug("Public key published chat={ChatId} participant={ParticipantId}", chatId, participantId);
    }

    public async Task SendMessage(EncryptedChatMessage message)
    {
        if (message is null || message.MessageId == Guid.Empty)
        {
            await RejectAsync(message?.MessageId ?? Guid.Empty, ChatMessageRejectReason.InvalidPayload);
            return;
        }

        if (Context.Items[ChatHubConstants.Items.ChatId] is not Guid chatId
            || Context.Items[ChatHubConstants.Items.ParticipantId] is not Guid participantId
            || Context.Items[ChatHubConstants.Items.SessionId] is not Guid sessionId)
        {
            Context.Abort();
            return;
        }

        if (!limiter.TryAcquire(participantId.ToString()).IsAllowed)
        {
            await RejectAsync(message.MessageId, ChatMessageRejectReason.RateLimited);
            return;
        }

        if (!TryDecodeBytes(
                message.CiphertextB64,
                minLen: 17,
                maxLen: ChatHubConstants.MaxCiphertextBytes,
                out var ciphertext)
            || !TryDecodeBytes(
                message.NonceB64,
                minLen: ChatHubConstants.NonceBytes,
                maxLen: ChatHubConstants.NonceBytes,
                out var nonce))
        {
            await RejectAsync(message.MessageId, ChatMessageRejectReason.DecodeFailed);
            return;
        }

        var response = await bus.InvokeAsync<AcceptChatMessageResponse>(
            new AcceptChatMessageCommand(
                sessionId, chatId, participantId,
                message.MessageId, message.Sequence, ciphertext, nonce));

        switch (response.Result)
        {
            case AcceptChatMessageResult.SessionRevoked:
                await Clients.Caller.SendAsync(ChatHubConstants.Events.SessionRevoked);
                Context.Abort();
                return;
            case AcceptChatMessageResult.SessionEnded:
                await RejectAsync(message.MessageId, ChatMessageRejectReason.SessionEnded);
                return;
            case AcceptChatMessageResult.Replay:
                logger.LogWarning("Replayed/out-of-order seq rejected chat={ChatId} participant={ParticipantId} seq={Seq}",
                    chatId, participantId, message.Sequence);
                await RejectAsync(message.MessageId, ChatMessageRejectReason.Replay);
                return;
            case AcceptChatMessageResult.Duplicate:
                await RejectAsync(message.MessageId, ChatMessageRejectReason.Duplicate);
                return;
            case AcceptChatMessageResult.Accepted:
                await scheduler.ScheduleIdleChecksAsync(chatId, response.Now);
                await Clients.OthersInGroup(ChatGroup(chatId))
                    .SendAsync(ChatHubConstants.Events.MessageReceived, ToWire(response.Message!));
                logger.LogDebug("ChatMessage stored chat={ChatId} sender={ParticipantId} seq={Seq}",
                    chatId, participantId, message.Sequence);
                return;
        }
    }

    public async Task EndChat()
    {
        if (Context.Items[ChatHubConstants.Items.ChatId] is not Guid chatId
            || Context.Items[ChatHubConstants.Items.SessionId] is not Guid sessionId)
        {
            Context.Abort();
            return;
        }

        var participant = await EnsureCurrentSessionAsync(sessionId);
        if (participant is null) return;
        var role = (Context.Items[ChatHubConstants.Items.Role] as string) ?? participant.Role.ToString();

        var response = await bus.InvokeAsync<EndChatResponse>(
            new EndChatCommand(chatId, EndChatReason.User, role, participant.Id));
        if (response.Result == EndChatResult.AlreadyEnded)
            await Clients.Caller.SendAsync(
                ChatHubConstants.Events.ChatEnded,
                new ChatEndedPayload(EndChatReason.AlreadyEnded.ToWire()));
        else if (response.Result == EndChatResult.Unauthorized)
        {
            // Defence in depth — hub guard above resolved this participant by token,
            // so a Unauthorized here implies tampering or stale connection.
            logger.LogWarning(
                "EndChat unauthorized for chat={ChatId} participant={ParticipantId}", chatId, participant.Id);
            await Clients.Caller.SendAsync(ChatHubConstants.Events.SessionRevoked);
            Context.Abort();
        }
        else if (response.Result == EndChatResult.Ended)
            logger.LogInformation("Chat {ChatId} ended by {Role} ({ParticipantId})", chatId, role, participant.Id);
    }

    private async Task<ChatParticipant?> EnsureCurrentSessionAsync(Guid sessionId)
    {
        var token = Context.GetHttpContext()?.Request.Query["token"].ToString() ?? string.Empty;
        var participant = await chats.GetByTokenAsync(token);
        if (participant is null || !participant.IsCurrentSession(sessionId))
        {
            await Clients.Caller.SendAsync(ChatHubConstants.Events.SessionRevoked);
            Context.Abort();
            return null;
        }
        return participant;
    }

    private Task RejectAsync(Guid messageId, ChatMessageRejectReason reason) =>
        Clients.Caller.SendAsync(
            ChatHubConstants.Events.MessageSendRejected,
            new ChatMessageRejected(messageId, reason));

    // Pre-check bounds the buffer allocation against length-attacks before
    // allocating ((b64.Length + 3) / 4) * 3 bytes.
    internal static bool TryDecodeBytes(string? b64, int minLen, int maxLen, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(b64)) return false;
        if (b64.Length > (maxLen / 3 + 1) * 4 + 4) return false;
        var buffer = new byte[((b64.Length + 3) / 4) * 3];
        if (!Convert.TryFromBase64String(b64, buffer, out var written)) return false;
        if (written < minLen || written > maxLen) return false;
        bytes = written == buffer.Length ? buffer : buffer[..written];
        return true;
    }

    private static object ToWire(ChatMessage msg) => new
    {
        id = msg.Id,
        participantId = msg.ParticipantId,
        ciphertextB64 = Convert.ToBase64String(msg.Ciphertext),
        nonceB64 = Convert.ToBase64String(msg.Nonce),
        sequence = msg.Sequence,
        createdAt = msg.CreatedAt
    };

    public static string ChatGroup(Guid chatId) => $"chat:{chatId:N}";
}
