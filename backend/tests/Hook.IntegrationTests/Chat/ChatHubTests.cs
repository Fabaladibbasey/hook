using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.OpenChat;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace Hook.IntegrationTests.Chat;

[Collection("Pipeline-3")]
public sealed class ChatHubTests : PipelineTestBase
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public ChatHubTests(DevPipelineFixture fx) : base(fx) { }

    private sealed record ChatHandle(
        Guid ChatId,
        OpenChatResponse Client,
        OpenChatResponse Provider,
        string ClientToken,
        string ProviderToken);

    private async Task<ChatHandle> SeedChatAsync()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<ChatSessionFactory>();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var clientPhone = UniquePhone();
        var providerPhone = UniquePhone();
        await factory.CreateAsync(clientPhone, providerPhone);
        await db.SaveChangesAsync();

        var participants = await db.ChatParticipants
            .Where(p => p.Phone == clientPhone || p.Phone == providerPhone)
            .ToListAsync();
        var clientP = participants.Single(p => p.Role == ChatParticipantRole.Client);
        var providerP = participants.Single(p => p.Role == ChatParticipantRole.Provider);

        var http = _fx.Factory.CreateClient();
        var clientOpen = await OpenAsync(http, clientP.Token);
        var providerOpen = await OpenAsync(http, providerP.Token);

        return new ChatHandle(clientP.ChatId, clientOpen, providerOpen, clientP.Token, providerP.Token);
    }

    private static async Task<OpenChatResponse> OpenAsync(HttpClient http, string token)
    {
        var resp = await http.GetFromJsonAsync<OpenChatResponse>($"/api/chat/open?token={Uri.EscapeDataString(token)}");
        resp.ShouldNotBeNull();
        return resp;
    }

    private HubConnection BuildHub(string token, Guid sessionId)
    {
        var hubUri = new Uri(
            _fx.Factory.Server.BaseAddress,
            $"hubs/chat?token={Uri.EscapeDataString(token)}&sessionId={sessionId}");
        var conn = new HubConnectionBuilder()
            .WithUrl(hubUri, opts =>
            {
                opts.HttpMessageHandlerFactory = _ => _fx.Factory.Server.CreateHandler();
                opts.Transports = HttpTransportType.LongPolling;
            })
            // Mirror server-side JsonStringEnumConverter so ChatMessageRejectReason
            // round-trips from the "Replay"/"Duplicate"/... wire form back into the enum.
            .AddJsonProtocol(opts =>
                opts.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        return conn;
    }

    private static TaskCompletionSource<T> Wait<T>(HubConnection conn, string method)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<T>(method, tcs.SetResult);
        return tcs;
    }

    // Filter out own-broadcast: the outbox-driven PeerKeyAvailable fan-out
    // includes the caller, frontend ignores its own id, integration tests do
    // the same.
    private static TaskCompletionSource<PeerKeyDto> WaitForPeer(HubConnection conn, Guid selfId)
    {
        var tcs = new TaskCompletionSource<PeerKeyDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<PeerKeyDto>(ChatHubConstants.Events.PeerKeyAvailable, dto =>
        {
            if (dto.PeerParticipantId == selfId) return;
            tcs.TrySetResult(dto);
        });
        return tcs;
    }

    private static async Task<T> Await<T>(TaskCompletionSource<T> tcs)
    {
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(Timeout));
        if (winner != tcs.Task) throw new TimeoutException($"Timed out waiting for {typeof(T).Name}");
        return await tcs.Task;
    }

    [Fact]
    public async Task OpenChat_ReturnsOutboundSequenceCursor_ZeroForFreshParticipant()
    {
        var chat = await SeedChatAsync();

        chat.Client.OutboundSequenceCursor.ShouldBe(0L);
        chat.Provider.OutboundSequenceCursor.ShouldBe(0L);
    }

    [Fact]
    public async Task RotateMidSession_NewTabContinuesFromCursor_NoCollision()
    {
        var chat = await SeedChatAsync();
        await using var clientConn = BuildHub(chat.ClientToken, chat.Client.SessionId);
        await using var providerConn = BuildHub(chat.ProviderToken, chat.Provider.SessionId);

        var providerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        providerConn.On<object>(ChatHubConstants.Events.HistoryLoaded, _ => providerReady.TrySetResult());

        await clientConn.StartAsync();
        await providerConn.StartAsync();
        (await Task.WhenAny(providerReady.Task, Task.Delay(Timeout))).ShouldBe(providerReady.Task);

        // Tab 1: send two messages so LastInboundSequence advances to 2.
        await clientConn.InvokeAsync("SendMessage",
            new EncryptedChatMessage(Guid.NewGuid(), Convert.ToBase64String(NewBytes(20)), Convert.ToBase64String(NewBytes(12)), Sequence: 1));
        await clientConn.InvokeAsync("SendMessage",
            new EncryptedChatMessage(Guid.NewGuid(), Convert.ToBase64String(NewBytes(20)), Convert.ToBase64String(NewBytes(12)), Sequence: 2));

        await WhatsappPipelineHelpers.WaitForConditionAsync(
            async () =>
            {
                await using var scope = _fx.Factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
                return await db.ChatMessages.AsNoTracking().CountAsync(m => m.ChatId == chat.ChatId) == 2;
            },
            timeout: TimeSpan.FromSeconds(3),
            description: "first two messages persisted");

        // Re-open chat URL — server rotates session AND publishes the LastInbound cursor.
        var http = _fx.Factory.CreateClient();
        var newOpen = await OpenAsync(http, chat.ClientToken);
        newOpen.SessionId.ShouldNotBe(chat.Client.SessionId);
        newOpen.OutboundSequenceCursor.ShouldBe(2L);

        // Tab 2: connect on rotated session, send from cursor + 1 — must be Accepted.
        await using var tab2 = BuildHub(chat.ClientToken, newOpen.SessionId);
        var rejects = new List<ChatMessageRejected>();
        tab2.On<ChatMessageRejected>(ChatHubConstants.Events.MessageSendRejected, dto => { lock (rejects) rejects.Add(dto); });
        await tab2.StartAsync();

        var nextSeq = newOpen.OutboundSequenceCursor + 1;
        await tab2.InvokeAsync("SendMessage",
            new EncryptedChatMessage(Guid.NewGuid(), Convert.ToBase64String(NewBytes(20)), Convert.ToBase64String(NewBytes(12)), Sequence: nextSeq));

        await WhatsappPipelineHelpers.WaitForConditionAsync(
            async () =>
            {
                await using var scope = _fx.Factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
                return await db.ChatMessages.AsNoTracking().CountAsync(m => m.ChatId == chat.ChatId) == 3;
            },
            timeout: TimeSpan.FromSeconds(3),
            description: "post-rotation send landed");

        lock (rejects) rejects.ShouldBeEmpty();
    }

    [Fact]
    public async Task Connect_WithRevokedSession_EmitsSessionRevokedOnFirstInvoke()
    {
        var chat = await SeedChatAsync();
        // Re-open the chat in a "different tab" — this rotates CurrentSessionId server-side.
        var http = _fx.Factory.CreateClient();
        await OpenAsync(http, chat.ClientToken);

        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conn = BuildHub(chat.ClientToken, chat.Client.SessionId);
        conn.On(ChatHubConstants.Events.SessionRevoked, () => revoked.TrySetResult());

        await conn.StartAsync();
        // Frontend invokes PublishKey immediately after StartAsync; that's where revocation surfaces.
        try { await conn.InvokeAsync("PublishKey", Convert.ToBase64String(TestKey(0xC1))); }
        catch { /* abort can surface mid-invoke */ }

        var winner = await Task.WhenAny(revoked.Task, Task.Delay(Timeout));
        winner.ShouldBe(revoked.Task);
    }

    [Fact]
    public async Task PublishKey_BothSides_DeliversPeerKeyAvailable()
    {
        var chat = await SeedChatAsync();
        await using var clientConn = BuildHub(chat.ClientToken, chat.Client.SessionId);
        await using var providerConn = BuildHub(chat.ProviderToken, chat.Provider.SessionId);

        // Broadcast PeerKeyAvailable now fans out to the whole chat group via the
        // outbox; the caller's own copy must be filtered out (matches the frontend
        // contract in useChatHub.ts). Filter by peerParticipantId != self id.
        var clientPeerKey = WaitForPeer(clientConn, chat.Client.ParticipantId);
        var providerPeerKey = WaitForPeer(providerConn, chat.Provider.ParticipantId);

        await clientConn.StartAsync();
        await providerConn.StartAsync();

        var clientKey = TestKey(0xC1);
        var providerKey = TestKey(0xC2);

        await clientConn.InvokeAsync("PublishKey", Convert.ToBase64String(clientKey));
        await providerConn.InvokeAsync("PublishKey", Convert.ToBase64String(providerKey));

        var providerSeen = await Await(providerPeerKey);
        var clientSeen = await Await(clientPeerKey);

        providerSeen.PeerParticipantId.ShouldBe(chat.Client.ParticipantId);
        Convert.FromBase64String(providerSeen.PeerPublicKeyB64).ShouldBe(clientKey);
        clientSeen.PeerParticipantId.ShouldBe(chat.Provider.ParticipantId);
        Convert.FromBase64String(clientSeen.PeerPublicKeyB64).ShouldBe(providerKey);
    }

    [Fact]
    public async Task SendMessage_DeliversToPeerNotSender()
    {
        var chat = await SeedChatAsync();
        await using var clientConn = BuildHub(chat.ClientToken, chat.Client.SessionId);
        await using var providerConn = BuildHub(chat.ProviderToken, chat.Provider.SessionId);

        var senderEcho = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedTcs = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clientConn.On<object>(ChatHubConstants.Events.MessageReceived, _ => senderEcho.TrySetResult());
        providerConn.On<WireMessage>(ChatHubConstants.Events.MessageReceived, m => receivedTcs.TrySetResult(m));
        providerConn.On<object>(ChatHubConstants.Events.HistoryLoaded, _ => providerReady.TrySetResult());

        await clientConn.StartAsync();
        await providerConn.StartAsync();

        // HistoryLoaded fires after Groups.AddToGroupAsync in ChatHub.OnConnectedAsync, so awaiting
        // it ensures the provider is in the broadcast group before the client sends.
        var ready = await Task.WhenAny(providerReady.Task, Task.Delay(Timeout));
        ready.ShouldBe(providerReady.Task);

        var dto = new EncryptedChatMessage(Guid.NewGuid(), Convert.ToBase64String(NewBytes(20)), Convert.ToBase64String(NewBytes(12)), Sequence: 1);
        await clientConn.InvokeAsync("SendMessage", dto);

        var received = await Await(receivedTcs);
        received.Id.ShouldBe(dto.MessageId);

        // Sender should not receive its own message back.
        var loser = await Task.WhenAny(senderEcho.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
        loser.ShouldNotBe(senderEcho.Task);
    }

    [Fact]
    public async Task SendMessage_ReplaySequence_RejectedAsReplay()
    {
        var chat = await SeedChatAsync();
        await using var conn = BuildHub(chat.ClientToken, chat.Client.SessionId);
        await conn.StartAsync();

        var rejects = new List<ChatMessageRejected>();
        conn.On<ChatMessageRejected>(ChatHubConstants.Events.MessageSendRejected, dto => { lock (rejects) rejects.Add(dto); });

        var first = new EncryptedChatMessage(Guid.NewGuid(), Convert.ToBase64String(NewBytes(20)), Convert.ToBase64String(NewBytes(12)), Sequence: 5);
        await conn.InvokeAsync("SendMessage", first);

        var second = first with { MessageId = Guid.NewGuid() };
        await conn.InvokeAsync("SendMessage", second);

        await WhatsappPipelineHelpers.WaitForConditionAsync(
            () =>
            {
                lock (rejects) return Task.FromResult(rejects.Any(r => r.MessageId == second.MessageId));
            },
            timeout: TimeSpan.FromSeconds(2),
            description: "Replay reject not received");
        lock (rejects)
        {
            var match = rejects.SingleOrDefault(r => r.MessageId == second.MessageId);
            match.ShouldNotBeNull();
            match!.Reason.ShouldBe(ChatMessageRejectReason.Replay);
        }

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var rowCount = await db.ChatMessages.AsNoTracking().CountAsync(m => m.ChatId == chat.ChatId);
        rowCount.ShouldBe(1);
    }

    [Fact]
    public async Task SendMessage_DuplicateMessageId_RejectedAsDuplicate()
    {
        var chat = await SeedChatAsync();
        await using var conn = BuildHub(chat.ClientToken, chat.Client.SessionId);
        await conn.StartAsync();

        var rejects = new List<ChatMessageRejected>();
        conn.On<ChatMessageRejected>(ChatHubConstants.Events.MessageSendRejected, dto => { lock (rejects) rejects.Add(dto); });

        var msgId = Guid.NewGuid();
        var first = new EncryptedChatMessage(msgId, Convert.ToBase64String(NewBytes(20)), Convert.ToBase64String(NewBytes(12)), Sequence: 1);
        await conn.InvokeAsync("SendMessage", first);

        var dup = new EncryptedChatMessage(msgId, Convert.ToBase64String(NewBytes(20)), Convert.ToBase64String(NewBytes(12)), Sequence: 2);
        await conn.InvokeAsync("SendMessage", dup);

        await WhatsappPipelineHelpers.WaitForConditionAsync(
            () =>
            {
                lock (rejects) return Task.FromResult(rejects.Any(r => r.MessageId == msgId && r.Reason == ChatMessageRejectReason.Duplicate));
            },
            timeout: TimeSpan.FromSeconds(2),
            description: "Duplicate reject not received");
        lock (rejects)
        {
            var match = rejects.SingleOrDefault(r => r.MessageId == msgId && r.Reason == ChatMessageRejectReason.Duplicate);
            match.ShouldNotBeNull();
        }

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var participant = await db.ChatParticipants.AsNoTracking().FirstAsync(p => p.Id == chat.Client.ParticipantId);
        participant.LastInboundSequence.ShouldBe(1);
        var rowCount = await db.ChatMessages.AsNoTracking().CountAsync(m => m.ChatId == chat.ChatId);
        rowCount.ShouldBe(1);
    }

    [Fact]
    public async Task SendMessage_AfterRotateSession_RejectedAsSessionRevoked()
    {
        var chat = await SeedChatAsync();
        await using var conn = BuildHub(chat.ClientToken, chat.Client.SessionId);
        await conn.StartAsync();

        // Simulate another tab opening, which rotates the participant's session.
        var http = _fx.Factory.CreateClient();
        await OpenAsync(http, chat.ClientToken);

        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On(ChatHubConstants.Events.SessionRevoked, () => revoked.TrySetResult());

        var dto = new EncryptedChatMessage(Guid.NewGuid(), Convert.ToBase64String(NewBytes(20)), Convert.ToBase64String(NewBytes(12)), Sequence: 1);
        try { await conn.InvokeAsync("SendMessage", dto); } catch { /* abort can surface mid-invoke */ }

        var winner = await Task.WhenAny(revoked.Task, Task.Delay(Timeout));
        winner.ShouldBe(revoked.Task);

        // Pin no-write-on-rejection: the stale-session send must not leak a
        // ChatMessage row past the SessionRevoked surface.
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var rowCount = await db.ChatMessages.AsNoTracking().CountAsync(m => m.ChatId == chat.ChatId);
        rowCount.ShouldBe(0);
    }

    [Fact]
    public async Task ChatMessages_DuplicateChatParticipantSequence_RejectedByDb()
    {
        var chat = await SeedChatAsync();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        var first = ChatMessage.Create(
            id: Guid.NewGuid(),
            chatId: chat.ChatId,
            participantId: chat.Client.ParticipantId,
            sequence: 1,
            ciphertext: new byte[32],
            nonce: new byte[12],
            now: DateTimeOffset.UtcNow);
        db.ChatMessages.Add(first);
        await db.SaveChangesAsync();

        var dup = ChatMessage.Create(
            id: Guid.NewGuid(),
            chatId: chat.ChatId,
            participantId: chat.Client.ParticipantId,
            sequence: 1,
            ciphertext: new byte[32],
            nonce: new byte[12],
            now: DateTimeOffset.UtcNow);
        db.ChatMessages.Add(dup);

        var ex = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = ex.InnerException.ShouldBeOfType<PostgresException>();
        pg.SqlState.ShouldBe("23505");
        pg.ConstraintName.ShouldBe(ChatHubConstants.SequenceUniqueIndexName);
    }

    private static string UniquePhone() => $"+220{Random.Shared.Next(0, 10_000_000):D7}";

    private static byte[] TestKey(byte _)
    {
        // Server now validates SPKI via ECDiffieHellman.ImportSubjectPublicKeyInfo —
        // a buffer-fill no longer parses. Each call returns a fresh P-256 SPKI so
        // PublishKey clears the parse gate.
        using var ecdh = System.Security.Cryptography.ECDiffieHellman.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return ecdh.ExportSubjectPublicKeyInfo();
    }

    private static byte[] NewBytes(int len)
    {
        var b = new byte[len];
        Random.Shared.NextBytes(b);
        return b;
    }

    private sealed record PeerKeyDto(Guid PeerParticipantId, string PeerPublicKeyB64);

    private sealed record WireMessage(
        Guid Id,
        Guid ParticipantId,
        string CiphertextB64,
        string NonceB64,
        long Sequence,
        DateTimeOffset CreatedAt);
}
