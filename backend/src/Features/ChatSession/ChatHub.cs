using Hook.Features.ChatLifecycle;
using Hook.Features.ChatSession.SessionAggregate;
using Microsoft.AspNetCore.SignalR;

namespace Hook.Features.ChatSession;

public sealed class ChatHub(IChatRepository chats, ChatScheduler scheduler, ILogger<ChatHub> logger, TimeProvider clock) : Hub
{
    private const int MaxCiphertextBytes = 5000;
    private const int NonceBytes = 12;
    private const int MaxPublicKeyBytes = 200;

    public override async Task OnConnectedAsync()
    {
        var token = Context.GetHttpContext()?.Request.Query["token"].ToString();
        var sessionIdRaw = Context.GetHttpContext()?.Request.Query["sessionId"].ToString();
        if (string.IsNullOrEmpty(token) || !Guid.TryParse(sessionIdRaw, out var sessionId))
        {
            Context.Abort();
            return;
        }

        var participant = await chats.GetByTokenAsync(token);
        if (participant is null || !participant.IsCurrentSession(sessionId))
        {
            await Clients.Caller.SendAsync("SessionRevoked");
            Context.Abort();
            return;
        }

        var session = await chats.GetSessionAsync(participant.ChatId);
        if (session is null || !session.CanSendMessage(clock.GetUtcNow()))
        {
            await Clients.Caller.SendAsync("SessionEnded");
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(participant.ChatId));
        Context.Items["chatId"] = participant.ChatId;
        Context.Items["participantId"] = participant.Id;
        Context.Items["sessionId"] = sessionId;
        Context.Items["role"] = participant.Role.ToString();

        var history = await chats.GetMessagesAsync(participant.ChatId, take: 50);
        await Clients.Caller.SendAsync("HistoryLoaded", history.Select(m => new
        {
            id = m.Id,
            participantId = m.ParticipantId,
            ciphertextB64 = Convert.ToBase64String(m.Ciphertext),
            nonceB64 = Convert.ToBase64String(m.Nonce),
            sequence = m.Sequence,
            createdAt = m.CreatedAt
        }));

        await base.OnConnectedAsync();
    }

    public async Task PublishKey(string publicKeyB64)
    {
        if (string.IsNullOrEmpty(publicKeyB64) || publicKeyB64.Length > MaxPublicKeyBytes * 2)
        {
            Context.Abort();
            return;
        }

        byte[] spki;
        try { spki = Convert.FromBase64String(publicKeyB64); }
        catch (FormatException) { Context.Abort(); return; }

        if (spki.Length is 0 or > MaxPublicKeyBytes)
        {
            Context.Abort();
            return;
        }

        if (Context.Items["chatId"] is not Guid chatId ||
            Context.Items["participantId"] is not Guid participantId)
        {
            Context.Abort();
            return;
        }

        var participant = await chats.GetParticipantAsync(participantId);
        if (participant is null) { Context.Abort(); return; }

        participant.SetPublicKey(spki);
        await chats.SaveChangesAsync();

        var peer = await chats.GetPeerAsync(chatId, participantId);
        if (peer?.PublicKey is { Length: > 0 } peerKey)
        {
            await Clients.Caller.SendAsync("PeerKeyAvailable", new
            {
                peerParticipantId = peer.Id,
                peerPublicKeyB64 = Convert.ToBase64String(peerKey)
            });

            await Clients.Group(GroupName(chatId)).SendAsync("PeerKeyAvailable", new
            {
                peerParticipantId = participant.Id,
                peerPublicKeyB64 = publicKeyB64
            });
        }

        logger.LogDebug("PublicKey published chat={ChatId} participant={ParticipantId}", chatId, participantId);
    }

    public async Task SendMessage(EncryptedMessageDto dto)
    {
        if (dto is null) return;

        byte[] ciphertext;
        byte[] nonce;
        try
        {
            ciphertext = Convert.FromBase64String(dto.CiphertextB64);
            nonce = Convert.FromBase64String(dto.NonceB64);
        }
        catch (FormatException) { return; }

        if (ciphertext.Length is 0 or > MaxCiphertextBytes) return;
        if (nonce.Length != NonceBytes) return;

        var chatId = (Guid)Context.Items["chatId"]!;
        var participantId = (Guid)Context.Items["participantId"]!;

        var participant = await chats.GetByTokenAsync(GetTokenFromQuery());
        if (participant is null) { Context.Abort(); return; }
        if (!participant.IsCurrentSession((Guid)Context.Items["sessionId"]!))
        {
            await Clients.Caller.SendAsync("SessionRevoked");
            Context.Abort();
            return;
        }

        if (!participant.TryAdvanceSequence(dto.Sequence))
        {
            logger.LogWarning("Replayed or out-of-order sequence rejected chat={ChatId} participant={ParticipantId} seq={Seq}",
                chatId, participantId, dto.Sequence);
            return;
        }

        var session = await chats.GetSessionAsync(chatId);
        if (session is null || !session.CanSendMessage(clock.GetUtcNow()))
        {
            await Clients.Caller.SendAsync("SessionEnded");
            return;
        }

        var msg = new ChatMessage
        {
            ChatId = chatId,
            ParticipantId = participantId,
            Ciphertext = ciphertext,
            Nonce = nonce,
            Sequence = dto.Sequence
        };

        await chats.AddMessageAsync(msg);
        var now = clock.GetUtcNow();
        session.Touch(now);
        await chats.SaveChangesAsync();
        await scheduler.ScheduleIdleChecksAsync(chatId, now);

        await Clients.Group(GroupName(chatId)).SendAsync("MessageReceived", new
        {
            id = msg.Id,
            participantId = msg.ParticipantId,
            ciphertextB64 = dto.CiphertextB64,
            nonceB64 = dto.NonceB64,
            sequence = msg.Sequence,
            createdAt = msg.CreatedAt
        });

        logger.LogDebug("ChatMessage stored chat={ChatId} participant={ParticipantId}", chatId, participantId);
    }

    public async Task EndChat()
    {
        if (Context.Items["chatId"] is not Guid chatId) { Context.Abort(); return; }
        var role = Context.Items["role"] as string ?? "Unknown";

        var participant = await chats.GetByTokenAsync(GetTokenFromQuery());
        if (participant is null) { Context.Abort(); return; }
        if (!participant.IsCurrentSession((Guid)Context.Items["sessionId"]!))
        {
            await Clients.Caller.SendAsync("SessionRevoked");
            Context.Abort();
            return;
        }

        var session = await chats.GetSessionAsync(chatId);
        if (session is null) return;
        if (session.Status != ChatSessionStatus.Active)
        {
            await Clients.Caller.SendAsync("ChatEnded", new { reason = "already-ended", endedBy = (string?)null });
            return;
        }

        session.End();
        await chats.SaveChangesAsync();

        await Clients.Group(GroupName(chatId)).SendAsync("ChatEnded",
            new { reason = "user", endedBy = role });

        logger.LogInformation("Chat {ChatId} ended by {Role} ({ParticipantId})", chatId, role, participant.Id);
    }

    private string GetTokenFromQuery() =>
        Context.GetHttpContext()?.Request.Query["token"].ToString() ?? string.Empty;

    public static string GroupName(Guid chatId) => $"chat:{chatId:N}";
}
