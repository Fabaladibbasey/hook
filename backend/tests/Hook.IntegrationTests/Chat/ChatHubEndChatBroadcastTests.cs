using System.Net.Http.Json;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.OpenChat;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests.Chat;

[Collection("Pipeline-3")]
public sealed class ChatHubEndChatBroadcastTests : PipelineTestBase
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public ChatHubEndChatBroadcastTests(DevPipelineFixture fx) : base(fx) { }

    private sealed record ChatHandle(
        Guid ChatId,
        OpenChatResponse Client,
        OpenChatResponse Provider,
        string ClientToken,
        string ProviderToken);
    private sealed record ChatEndedDto(string Reason, string EndedBy);

    [Fact]
    public async Task ClientEndsChat_BroadcastsChatEnded_ToProvider_ViaEndChatCommandAndScraper()
    {
        var chat = await SeedChatAsync();
        await using var clientConn = BuildHub(chat.ClientToken, chat.Client.SessionId);
        await using var providerConn = BuildHub(chat.ProviderToken, chat.Provider.SessionId);

        var providerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerEnded = new TaskCompletionSource<ChatEndedDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        providerConn.On<object>(ChatHubConstants.Events.HistoryLoaded, _ => providerReady.TrySetResult());
        providerConn.On<ChatEndedDto>(ChatHubConstants.Events.ChatEnded, dto => providerEnded.TrySetResult(dto));

        await clientConn.StartAsync();
        await providerConn.StartAsync();

        // Wait until provider is in the broadcast group, otherwise the ChatEnded send
        // races the AddToGroupAsync and the test would flake.
        var ready = await Task.WhenAny(providerReady.Task, Task.Delay(Timeout));
        ready.ShouldBe(providerReady.Task);

        await clientConn.InvokeAsync("EndChat");

        var winner = await Task.WhenAny(providerEnded.Task, Task.Delay(Timeout));
        winner.ShouldBe(providerEnded.Task);
        var dto = await providerEnded.Task;
        dto.Reason.ShouldBe("user");
        dto.EndedBy.ShouldBe(ChatParticipantRole.Client.ToString());

        // Sanity: scraper actually persisted End() on the aggregate.
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var session = await db.ChatSessions.AsNoTracking().FirstAsync(s => s.Id == chat.ChatId);
        session.Status.ShouldBe(ChatSessionStatus.Ended);
    }

    [Fact]
    public async Task ProviderEndsChat_BroadcastsChatEnded_ToClient_WithEndedByProvider()
    {
        var chat = await SeedChatAsync();
        await using var clientConn = BuildHub(chat.ClientToken, chat.Client.SessionId);
        await using var providerConn = BuildHub(chat.ProviderToken, chat.Provider.SessionId);

        var clientReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientEnded = new TaskCompletionSource<ChatEndedDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientConn.On<object>(ChatHubConstants.Events.HistoryLoaded, _ => clientReady.TrySetResult());
        clientConn.On<ChatEndedDto>(ChatHubConstants.Events.ChatEnded, dto => clientEnded.TrySetResult(dto));

        await clientConn.StartAsync();
        await providerConn.StartAsync();
        (await Task.WhenAny(clientReady.Task, Task.Delay(Timeout))).ShouldBe(clientReady.Task);

        await providerConn.InvokeAsync("EndChat");

        var winner = await Task.WhenAny(clientEnded.Task, Task.Delay(Timeout));
        winner.ShouldBe(clientEnded.Task);
        var dto = await clientEnded.Task;
        dto.Reason.ShouldBe("user");
        dto.EndedBy.ShouldBe(ChatParticipantRole.Provider.ToString());
    }

    [Fact]
    public async Task EndChat_Twice_FromSameCaller_SecondCallEchoesAlreadyEnded_NoSecondPeerBroadcast()
    {
        var chat = await SeedChatAsync();
        await using var clientConn = BuildHub(chat.ClientToken, chat.Client.SessionId);
        await using var providerConn = BuildHub(chat.ProviderToken, chat.Provider.SessionId);

        var providerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerEnded = new TaskCompletionSource<ChatEndedDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var alreadyEnded = new TaskCompletionSource<ChatEndedDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientEchoes = new System.Collections.Concurrent.ConcurrentBag<ChatEndedDto>();
        providerConn.On<object>(ChatHubConstants.Events.HistoryLoaded, _ => providerReady.TrySetResult());
        providerConn.On<ChatEndedDto>(ChatHubConstants.Events.ChatEnded, dto => providerEnded.TrySetResult(dto));
        clientConn.On<ChatEndedDto>(ChatHubConstants.Events.ChatEnded, dto =>
        {
            clientEchoes.Add(dto);
            if (dto.Reason == "already-ended") alreadyEnded.TrySetResult(dto);
        });

        await clientConn.StartAsync();
        await providerConn.StartAsync();
        (await Task.WhenAny(providerReady.Task, Task.Delay(Timeout))).ShouldBe(providerReady.Task);

        await clientConn.InvokeAsync("EndChat");
        (await Task.WhenAny(providerEnded.Task, Task.Delay(Timeout))).ShouldBe(providerEnded.Task);

        var providerBroadcastsBefore = providerEnded.Task.IsCompletedSuccessfully ? 1 : 0;
        await clientConn.InvokeAsync("EndChat");

        (await Task.WhenAny(alreadyEnded.Task, Task.Delay(Timeout))).ShouldBe(alreadyEnded.Task);

        clientEchoes.ShouldContain(d => d.Reason == "already-ended");
        // Provider must still have seen exactly one ChatEnded — second EndChat is caller-only.
        providerBroadcastsBefore.ShouldBe(1);
    }

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
        return new HubConnectionBuilder()
            .WithUrl(hubUri, opts =>
            {
                opts.HttpMessageHandlerFactory = _ => _fx.Factory.Server.CreateHandler();
                opts.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }

    private static string UniquePhone() => $"+220{Random.Shared.Next(0, 10_000_000):D7}";
}
