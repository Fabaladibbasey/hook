using System.Net.Http.Json;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests.Chat;

[Collection("Pipeline-3")]
public sealed class ChatHubTests : PipelineTestBase
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public ChatHubTests(DevPipelineFixture fx) : base(fx) { }

    private sealed record OpenResponse(Guid ChatId, Guid ParticipantId, string Role, Guid SessionId, string Status);

    private sealed record ChatHandle(
        Guid ChatId,
        OpenResponse Client,
        OpenResponse Provider,
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

    private static async Task<OpenResponse> OpenAsync(HttpClient http, string token)
    {
        var resp = await http.GetFromJsonAsync<OpenResponse>($"/api/chat/open?token={Uri.EscapeDataString(token)}");
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
            .Build();
        return conn;
    }

    private static TaskCompletionSource<T> Wait<T>(HubConnection conn, string method)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<T>(method, tcs.SetResult);
        return tcs;
    }

    private static async Task<T> Await<T>(TaskCompletionSource<T> tcs)
    {
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(Timeout));
        if (winner != tcs.Task) throw new TimeoutException($"Timed out waiting for {typeof(T).Name}");
        return await tcs.Task;
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

        var clientPeerKey = Wait<PeerKeyDto>(clientConn, ChatHubConstants.Events.PeerKeyAvailable);
        var providerPeerKey = Wait<PeerKeyDto>(providerConn, ChatHubConstants.Events.PeerKeyAvailable);

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
    }

    private static string UniquePhone() => $"+220{Random.Shared.Next(0, 10_000_000):D7}";

    private static byte[] TestKey(byte fill)
    {
        var key = new byte[91]; // SPKI for P-256 is ~91 bytes; size is just for the byte cap, hub doesn't validate curve.
        Array.Fill(key, fill);
        return key;
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
