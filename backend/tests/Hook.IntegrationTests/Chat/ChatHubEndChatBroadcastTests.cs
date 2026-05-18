using System.Net.Http.Json;
using Hook.Features.ChatSession;
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

    private sealed record OpenResponse(Guid ChatId, Guid ParticipantId, string Role, Guid SessionId, string Status);
    private sealed record ChatHandle(Guid ChatId, OpenResponse Client, OpenResponse Provider, string ClientToken, string ProviderToken);
    private sealed record ChatEndedDto(string Reason, string? EndedBy);

    [Fact]
    public async Task EndChat_BroadcastsChatEnded_ToPeer_ViaDomainEventScraper()
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
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_fx.Factory.Server.BaseAddress, $"hubs/chat?token={Uri.EscapeDataString(token)}&sessionId={sessionId}"), opts =>
            {
                opts.HttpMessageHandlerFactory = _ => _fx.Factory.Server.CreateHandler();
                opts.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }

    private static string UniquePhone() => $"+1415{Guid.NewGuid().ToString("N")[..7]}";
}
