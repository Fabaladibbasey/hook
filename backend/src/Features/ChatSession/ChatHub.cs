using Hook.Features.ChatLifecycle;
using Hook.Features.ChatSession.SessionAggregate;
using Microsoft.AspNetCore.SignalR;

namespace Hook.Features.ChatSession;

public sealed class ChatHub(IChatRepository chats, ChatScheduler scheduler, ILogger<ChatHub> logger, TimeProvider clock) : Hub
{
    private const int MaxCiphertextBytes = 5000;
    private const int NonceBytes = 12;
    private const int MaxPublicKeyBytes = 200;
    private const int MaxRecipients = 16;

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var token = http?.Request.Query["token"].ToString();
        var sessionIdRaw = http?.Request.Query["sessionId"].ToString();
        var deviceIdRaw = http?.Request.Query["deviceId"].ToString();
        if (string.IsNullOrEmpty(token)
            || !Guid.TryParse(sessionIdRaw, out var sessionId)
            || !Guid.TryParse(deviceIdRaw, out var deviceId))
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

        await Groups.AddToGroupAsync(Context.ConnectionId, ChatGroup(participant.ChatId));
        await Groups.AddToGroupAsync(Context.ConnectionId, DeviceGroup(participant.ChatId, deviceId));
        Context.Items["chatId"] = participant.ChatId;
        Context.Items["participantId"] = participant.Id;
        Context.Items["sessionId"] = sessionId;
        Context.Items["deviceId"] = deviceId;
        Context.Items["role"] = participant.Role.ToString();

        var deviceKeys = await chats.GetDeviceKeysAsync(participant.ChatId);
        await Clients.Caller.SendAsync("DeviceKeysSnapshot", new
        {
            devices = deviceKeys.Select(k => new
            {
                participantId = k.ParticipantId,
                deviceId = k.DeviceId,
                publicKeyB64 = Convert.ToBase64String(k.PublicKey)
            })
        });

        var history = await chats.GetMessagesForDeviceAsync(participant.ChatId, deviceId, take: 50);
        await Clients.Caller.SendAsync("HistoryLoaded", history.Select(row => new
        {
            id = row.Header.Id,
            participantId = row.Header.ParticipantId,
            senderDeviceId = row.Header.SenderDeviceId,
            ciphertextB64 = Convert.ToBase64String(row.Envelope.Ciphertext),
            nonceB64 = Convert.ToBase64String(row.Envelope.Nonce),
            sequence = row.Header.Sequence,
            createdAt = row.Header.CreatedAt
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

        if (spki.Length is 0 or > MaxPublicKeyBytes) { Context.Abort(); return; }

        if (Context.Items["chatId"] is not Guid chatId
            || Context.Items["participantId"] is not Guid participantId
            || Context.Items["deviceId"] is not Guid deviceId)
        {
            Context.Abort();
            return;
        }

        await chats.UpsertDeviceKeyAsync(chatId, participantId, deviceId, spki, clock.GetUtcNow());
        await chats.SaveChangesAsync();

        await Clients.Group(ChatGroup(chatId)).SendAsync("PeerDeviceKeyAvailable", new
        {
            participantId,
            deviceId,
            publicKeyB64
        });

        logger.LogDebug("Device key published chat={ChatId} participant={ParticipantId} device={DeviceId}",
            chatId, participantId, deviceId);
    }

    public async Task SendMessage(SendMessageDto dto)
    {
        if (dto?.Recipients is null || dto.Recipients.Count is 0 or > MaxRecipients) return;

        if (Context.Items["chatId"] is not Guid chatId
            || Context.Items["participantId"] is not Guid participantId
            || Context.Items["deviceId"] is not Guid senderDeviceId
            || Context.Items["sessionId"] is not Guid sessionId)
        {
            Context.Abort();
            return;
        }

        var senderKey = await chats.GetDeviceKeyAsync(participantId, senderDeviceId);
        if (senderKey is null) { Context.Abort(); return; }

        var participant = await chats.GetByTokenAsync(GetTokenFromQuery());
        if (participant is null || !participant.IsCurrentSession(sessionId))
        {
            await Clients.Caller.SendAsync("SessionRevoked");
            Context.Abort();
            return;
        }

        if (!senderKey.TryAdvanceSequence(dto.Sequence))
        {
            logger.LogWarning("Replayed/out-of-order seq rejected chat={ChatId} device={DeviceId} seq={Seq}",
                chatId, senderDeviceId, dto.Sequence);
            return;
        }

        var session = await chats.GetSessionAsync(chatId);
        if (session is null || !session.CanSendMessage(clock.GetUtcNow()))
        {
            await Clients.Caller.SendAsync("SessionEnded");
            return;
        }

        var allDeviceKeys = await chats.GetDeviceKeysAsync(chatId);
        var validDeviceIds = allDeviceKeys.Select(k => k.DeviceId).ToHashSet();

        var recipients = new List<ChatMessageRecipient>();
        var messageId = Guid.NewGuid();
        foreach (var r in dto.Recipients)
        {
            if (!validDeviceIds.Contains(r.DeviceId)) return;
            byte[] ciphertext, nonce;
            try
            {
                ciphertext = Convert.FromBase64String(r.CiphertextB64);
                nonce = Convert.FromBase64String(r.NonceB64);
            }
            catch (FormatException) { return; }
            if (ciphertext.Length is 0 or > MaxCiphertextBytes) return;
            if (nonce.Length != NonceBytes) return;
            recipients.Add(new ChatMessageRecipient
            {
                MessageId = messageId,
                RecipientDeviceId = r.DeviceId,
                Ciphertext = ciphertext,
                Nonce = nonce
            });
        }

        var msg = new ChatMessage
        {
            Id = messageId,
            ChatId = chatId,
            ParticipantId = participantId,
            SenderDeviceId = senderDeviceId,
            Sequence = dto.Sequence,
            Recipients = recipients
        };

        await chats.AddMessageAsync(msg);
        var now = clock.GetUtcNow();
        session.Touch(now);
        await chats.SaveChangesAsync();
        await scheduler.ScheduleIdleChecksAsync(chatId, now);

        foreach (var rcp in recipients)
        {
            await Clients.Group(DeviceGroup(chatId, rcp.RecipientDeviceId)).SendAsync("MessageReceived", new
            {
                id = msg.Id,
                participantId = msg.ParticipantId,
                senderDeviceId = msg.SenderDeviceId,
                ciphertextB64 = Convert.ToBase64String(rcp.Ciphertext),
                nonceB64 = Convert.ToBase64String(rcp.Nonce),
                sequence = msg.Sequence,
                createdAt = msg.CreatedAt
            });
        }

        logger.LogDebug("ChatMessage stored chat={ChatId} sender={ParticipantId}/{DeviceId} recipients={N}",
            chatId, participantId, senderDeviceId, recipients.Count);
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

        session.End(clock.GetUtcNow());
        await chats.SaveChangesAsync();

        await Clients.Group(ChatGroup(chatId)).SendAsync("ChatEnded", new { reason = "user", endedBy = role });
        logger.LogInformation("Chat {ChatId} ended by {Role} ({ParticipantId})", chatId, role, participant.Id);
    }

    private string GetTokenFromQuery() =>
        Context.GetHttpContext()?.Request.Query["token"].ToString() ?? string.Empty;

    public static string ChatGroup(Guid chatId) => $"chat:{chatId:N}";
    public static string DeviceGroup(Guid chatId, Guid deviceId) => $"chat:{chatId:N}:device:{deviceId:N}";
}
