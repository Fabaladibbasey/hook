using System.Net.Http.Json;
using Hook.Features.ChatLifecycle.EndChat;
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
public sealed class NonHandlerContextEventLossTests : PipelineTestBase
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(5);

    public NonHandlerContextEventLossTests(DevPipelineFixture fx) : base(fx) { }

    private sealed record OpenResponse(Guid ChatId, Guid ParticipantId, string Role, Guid SessionId, string Status);
    private sealed record ChatEndedDto(string Reason, string EndedBy);

    [Fact]
    public async Task End_OutsideHandlerContext_DoesNotBroadcast_ToPeer()
    {
        var (chatId, providerToken, providerSessionId) = await SeedAndConnectPeerAsync();

        await using var providerConn = BuildHub(providerToken, providerSessionId);
        var providerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerEnded = new TaskCompletionSource<ChatEndedDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        providerConn.On<object>(ChatHubConstants.Events.HistoryLoaded, _ => providerReady.TrySetResult());
        providerConn.On<ChatEndedDto>(ChatHubConstants.Events.ChatEnded, dto => providerEnded.TrySetResult(dto));
        await providerConn.StartAsync();
        (await Task.WhenAny(providerReady.Task, Task.Delay(ReadyTimeout))).ShouldBe(providerReady.Task);

        // Mutate the aggregate from a direct DI scope — no Wolverine handler, no AutoApplyTransactions.
        // CLAUDE.md bans this pattern; this test pins the silent-loss behavior so a future fix
        // (e.g. scope-aware EF interception) would flip the assertion.
        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var session = await db.ChatSessions.FirstAsync(s => s.Id == chatId);
            session.End(DateTimeOffset.UtcNow, EndChatReason.User, ChatParticipantRole.Client.ToString());
            await db.SaveChangesAsync();
        }

        var fired = await Task.WhenAny(providerEnded.Task, Task.Delay(Quiet));
        fired.ShouldNotBe(providerEnded.Task,
            "Mutation outside a Wolverine handler must NOT produce a ChatEnded broadcast — DomainEventScraper only drains inside AutoApplyTransactions.");

        // Aggregate state DID persist — proves SaveChanges succeeded; only the event was lost.
        await using var verify = _fx.Factory.Services.CreateAsyncScope();
        var db2 = verify.ServiceProvider.GetRequiredService<HookDbContext>();
        var persisted = await db2.ChatSessions.AsNoTracking().FirstAsync(s => s.Id == chatId);
        persisted.Status.ShouldBe(ChatSessionStatus.Ended);
    }

    private async Task<(Guid chatId, string providerToken, Guid providerSessionId)> SeedAndConnectPeerAsync()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<ChatSessionFactory>();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var clientPhone = UniquePhone();
        var providerPhone = UniquePhone();
        await factory.CreateAsync(clientPhone, providerPhone);
        await db.SaveChangesAsync();

        var providerP = await db.ChatParticipants
            .Where(p => p.Phone == providerPhone && p.Role == ChatParticipantRole.Provider)
            .SingleAsync();

        var http = _fx.Factory.CreateClient();
        var providerOpen = await OpenAsync(http, providerP.Token);
        return (providerP.ChatId, providerP.Token, providerOpen.SessionId);
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

    private static string UniquePhone() => $"+220{Random.Shared.Next(0, 10_000_000):D7}";
}
