using Hook.Features.ChatLifecycle.EndChat;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests.Chat;

[Collection("Pipeline-4")]
public sealed class ChatRepositoryTests : PipelineTestBase
{
    public ChatRepositoryTests(DevPipelineFixture fx) : base(fx) { }

    private static string UniquePhone() => $"+220{Random.Shared.Next(0, 10_000_000):D7}";

    private async Task<(Features.ChatSession.SessionAggregate.ChatSession Session, ChatParticipant Client, ChatParticipant Provider)>
        SeedSessionAsync(HookDbContext db)
    {
        var session = Features.ChatSession.SessionAggregate.ChatSession.Create(
            TimeSpan.FromHours(24), DateTimeOffset.UtcNow);
        var client = ChatParticipant.Create(session.Id, ChatParticipantRole.Client, UniquePhone());
        var provider = ChatParticipant.Create(session.Id, ChatParticipantRole.Provider, UniquePhone());
        db.ChatSessions.Add(session);
        db.ChatParticipants.AddRange(client, provider);
        await db.SaveChangesAsync();
        return (session, client, provider);
    }

    private static void SeedMessage(HookDbContext db, Guid chatId, Guid participantId, long seq) =>
        db.ChatMessages.Add(ChatMessage.Create(
            Guid.CreateVersion7(), chatId, participantId, seq,
            new byte[20], new byte[12], DateTimeOffset.UtcNow));

    [Fact]
    public async Task GetMessageCountByRoleAsync_AsymmetricRoles_ReturnsBothCounts()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IChatRepository>();
        var (session, client, provider) = await SeedSessionAsync(db);

        SeedMessage(db, session.Id, client.Id, 1);
        SeedMessage(db, session.Id, client.Id, 2);
        SeedMessage(db, session.Id, client.Id, 3);
        SeedMessage(db, session.Id, provider.Id, 4);
        await db.SaveChangesAsync();

        var (clientCount, providerCount) = await repo.GetMessageCountByRoleAsync(session.Id);

        clientCount.ShouldBe(3);
        providerCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetMessageCountByRoleAsync_NoMessages_ReturnsZeroes()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IChatRepository>();
        var (session, _, _) = await SeedSessionAsync(db);

        var (clientCount, providerCount) = await repo.GetMessageCountByRoleAsync(session.Id);

        clientCount.ShouldBe(0);
        providerCount.ShouldBe(0);
    }

    [Fact]
    public async Task TryMarkProductiveSilenceAsync_FirstCall_Wins_SecondCall_ReturnsFalse()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IChatRepository>();
        var (session, _, _) = await SeedSessionAsync(db);

        var now = DateTimeOffset.UtcNow;
        Assert.True(await repo.TryMarkProductiveSilenceAsync(session.Id, now));
        Assert.False(await repo.TryMarkProductiveSilenceAsync(session.Id, now.AddSeconds(1)));

        await using var verifyScope = _fx.Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<HookDbContext>();
        var loaded = await verifyDb.ChatSessions.AsNoTracking().FirstAsync(c => c.Id == session.Id);
        loaded.ProductiveSilenceFiredAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task TryMarkProductiveSilenceAsync_OnEndedSession_ReturnsFalse()
    {
        Guid chatId;
        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var (session, _, _) = await SeedSessionAsync(db);
            chatId = session.Id;
            session.End(DateTimeOffset.UtcNow, EndChatReason.User, "Client");
            await db.SaveChangesAsync();
        }

        await using var assertScope = _fx.Factory.Services.CreateAsyncScope();
        var repo = assertScope.ServiceProvider.GetRequiredService<IChatRepository>();

        Assert.False(await repo.TryMarkProductiveSilenceAsync(chatId, DateTimeOffset.UtcNow));
    }
}
