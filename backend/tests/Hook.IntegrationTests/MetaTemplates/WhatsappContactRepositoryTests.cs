using Hook.Features.MetaTemplates;
using Hook.Features.Tips;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests.MetaTemplates;

[Collection("Pipeline-1")]
public sealed class WhatsappContactRepositoryTests : PipelineTestBase
{
    public WhatsappContactRepositoryTests(DevPipelineFixture fx) : base(fx) { }

    private static string UniquePhone() => $"+220{Random.Shared.Next(0, 10_000_000):D7}";

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
    public async Task GetForTipsAsync_UnknownPhone_ReturnsNull()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhatsappContactRepository>();

        var state = await repo.GetForTipsAsync(UniquePhone());

        state.ShouldBeNull();
    }

    [Fact]
    public async Task GetForTipsAsync_FreshContact_ReturnsEmptyCooldownDict()
    {
        var phone = UniquePhone();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhatsappContactRepository>();
        await repo.UpsertInboundAsync(phone, DateTimeOffset.UtcNow);

        var state = await repo.GetForTipsAsync(phone);

        state.ShouldNotBeNull();
        state!.Cooldowns.ShouldBeEmpty();
    }

    [Fact]
    public async Task RecordTipAsync_PersistsTriggerAndTimestamp()
    {
        var phone = UniquePhone();
        var at = DateTimeOffset.UtcNow;
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhatsappContactRepository>();
        await repo.UpsertInboundAsync(phone, at);

        await repo.RecordTipAsync(phone, TipTrigger.AfterWelcome, at);

        var state = await repo.GetForTipsAsync(phone);
        state.ShouldNotBeNull();
        state!.Cooldowns.ShouldContainKey(TipTrigger.AfterWelcome);
        state.Cooldowns[TipTrigger.AfterWelcome].ShouldBe(at, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task RecordTipAsync_TwoTriggers_BothPersistedIndependently()
    {
        // Confirms jsonb_set keys by trigger ordinal — a second RecordTip on a
        // different trigger must MERGE rather than replace the dict. Round-trip
        // through the EF ValueConverter + ValueComparer wires up end-to-end.
        var phone = UniquePhone();
        var at1 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var at2 = DateTimeOffset.UtcNow;
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhatsappContactRepository>();
        await repo.UpsertInboundAsync(phone, at2);

        await repo.RecordTipAsync(phone, TipTrigger.AfterWelcome, at1);
        await repo.RecordTipAsync(phone, TipTrigger.UserRequested, at2);

        var state = await repo.GetForTipsAsync(phone);
        state.ShouldNotBeNull();
        state!.Cooldowns.Count.ShouldBe(2);
        state.Cooldowns[TipTrigger.AfterWelcome].ShouldBe(at1, TimeSpan.FromMilliseconds(1));
        state.Cooldowns[TipTrigger.UserRequested].ShouldBe(at2, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task RecordTipAsync_SameTriggerTwice_OverwritesTimestamp()
    {
        var phone = UniquePhone();
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-30);
        var later = DateTimeOffset.UtcNow;
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhatsappContactRepository>();
        await repo.UpsertInboundAsync(phone, later);

        await repo.RecordTipAsync(phone, TipTrigger.AfterWelcome, earlier);
        await repo.RecordTipAsync(phone, TipTrigger.AfterWelcome, later);

        var state = await repo.GetForTipsAsync(phone);
        state.ShouldNotBeNull();
        state!.Cooldowns[TipTrigger.AfterWelcome].ShouldBe(later, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task GetPreferredLocaleAsync_UnknownPhone_ReturnsEmpty()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhatsappContactRepository>();

        var locale = await repo.GetPreferredLocaleAsync(UniquePhone());

        locale.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task UpdatePreferredLocaleAsync_PersistsLocale()
    {
        var phone = UniquePhone();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhatsappContactRepository>();
        await repo.UpsertInboundAsync(phone, DateTimeOffset.UtcNow);

        await repo.UpdatePreferredLocaleAsync(phone, "fr");

        (await repo.GetPreferredLocaleAsync(phone)).ShouldBe("fr");
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
