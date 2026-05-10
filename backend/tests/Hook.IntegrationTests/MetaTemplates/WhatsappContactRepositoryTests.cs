using Hook.Features.MetaTemplates;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests.MetaTemplates;

[Collection("Pipeline-1")]
public sealed class WhatsappContactRepositoryTests : PipelineTestBase
{
    public WhatsappContactRepositoryTests(DevPipelineFixture fx) : base(fx) { }

    private static string UniquePhone() => $"+220{Guid.NewGuid().ToString("N")[..8]}";

    [Fact]
    public async Task UpsertInboundAsync_NewContact_Inserts()
    {
        var phone = UniquePhone();
        var at = DateTimeOffset.UtcNow;

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhatsappContactRepository>();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        await repo.UpsertInboundAsync(phone, at);

        var loaded = await db.WhatsappContacts.AsNoTracking().FirstOrDefaultAsync(c => c.Phone == phone);
        loaded.ShouldNotBeNull();
        // Postgres truncates timestamps to microseconds; tolerate that round-trip.
        loaded!.LastInboundAt.ShouldBe(at, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task UpsertInboundAsync_LaterTimestamp_AdvancesLastInboundAt()
    {
        var phone = UniquePhone();
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-5);
        var later = DateTimeOffset.UtcNow;

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhatsappContactRepository>();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        await repo.UpsertInboundAsync(phone, earlier);
        await repo.UpsertInboundAsync(phone, later);

        var loaded = await db.WhatsappContacts.AsNoTracking().FirstAsync(c => c.Phone == phone);
        loaded.LastInboundAt.ShouldBe(later, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task UpsertInboundAsync_OutOfOrderEarlierTimestamp_DoesNotMoveBackwards()
    {
        // GREATEST() in the ON CONFLICT clause must keep the newest timestamp even
        // when WhatsApp delivers events out of order.
        var phone = UniquePhone();
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-5);
        var later = DateTimeOffset.UtcNow;

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhatsappContactRepository>();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        await repo.UpsertInboundAsync(phone, later);
        await repo.UpsertInboundAsync(phone, earlier);

        var loaded = await db.WhatsappContacts.AsNoTracking().FirstAsync(c => c.Phone == phone);
        loaded.LastInboundAt.ShouldBe(later, TimeSpan.FromMilliseconds(1));
    }
}
