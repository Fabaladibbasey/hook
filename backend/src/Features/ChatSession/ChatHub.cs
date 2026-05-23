using Hook.Features.ChatLifecycle;
using Hook.Features.ChatLifecycle.EndChat;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Shared.Persistence.Data;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.AspNetCore.SignalR;
using Wolverine;

namespace Hook.Features.ChatSession;

public sealed class ChatHub(
    IChatRepository chats,
    HookDbContext db,
    ChatScheduler scheduler,
    IMessageBus bus,
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

        var participant = await EnsureCurrentSessionAsync(sessionId);
        if (participant is null) return;

        participant.SetPublicKey(spki);
        await db.SaveChangesAsync();

        var peer = await chats.GetPeerAsync(chatId, participantId);
        if (peer?.PublicKey is { Length: > 0 } peerKey)
        {
            await Clients.Caller.SendAsync(ChatHubConstants.Events.PeerKeyAvailable, new
            {
                peerParticipantId = peer.Id,
                peerPublicKeyB64 = Convert.ToBase64String(peerKey)
            });
        }

        await Clients.OthersInGroup(ChatGroup(chatId)).SendAsync(ChatHubConstants.Events.PeerKeyAvailable, new
        {
            peerParticipantId = participantId,
            // Re-encode from the decoded SPKI rather than echoing the client's
            // b64 string. Kills cross-browser whitespace drift + matches the
            // peer?.PublicKey path above that always re-encodes.
            peerPublicKeyB64 = Convert.ToBase64String(spki)
        });

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

        var participant = await EnsureCurrentSessionAsync(sessionId);
        if (participant is null) return;

        var session = await chats.GetSessionAsync(chatId);
        if (session is null || !session.CanSendMessage(clock.GetUtcNow()))
        {
            await RejectAsync(message.MessageId, ChatMessageRejectReason.SessionEnded);
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

        var msg = ChatMessage.Create(
            id: message.MessageId,
            chatId: chatId,
            participantId: participantId,
            sequence: message.Sequence,
            ciphertext: ciphertext,
            nonce: nonce,
            now: clock.GetUtcNow());

        var inserted = await chats.TryAddMessageAsync(msg);
        if (!inserted)
        {
            await RejectAsync(message.MessageId, ChatMessageRejectReason.Duplicate);
            return;
        }

        if (!participant.TryAdvanceSequence(message.Sequence))
        {
            logger.LogWarning("Replayed/out-of-order seq rejected chat={ChatId} participant={ParticipantId} seq={Seq}",
                chatId, participantId, message.Sequence);
            await RejectAsync(message.MessageId, ChatMessageRejectReason.Replay);
            return;
        }

        var now = clock.GetUtcNow();
        session.Touch(now);
        await db.SaveChangesAsync();
        await scheduler.ScheduleIdleChecksAsync(chatId, now);

        await Clients.OthersInGroup(ChatGroup(chatId)).SendAsync(ChatHubConstants.Events.MessageReceived, ToWire(msg));

        logger.LogDebug("ChatMessage stored chat={ChatId} sender={ParticipantId} seq={Seq}",
            chatId, participantId, message.Sequence);
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

        var response = await bus.InvokeAsync<EndChatResponse>(new EndChatCommand(chatId, EndChatReason.User, role));
        if (response.Result == EndChatResult.AlreadyEnded)
            await Clients.Caller.SendAsync(
                ChatHubConstants.Events.ChatEnded,
                new ChatEndedPayload(EndChatReason.AlreadyEnded.ToWire()));
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
