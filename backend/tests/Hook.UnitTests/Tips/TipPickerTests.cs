using Hook.Features.MetaTemplates;
using Hook.Features.Tips;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Hook.UnitTests.Tips;

public class TipPickerTests
{
    private readonly Mock<IWhatsappContactRepository> _contacts = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));

    private TipPicker Build(TipOptions? opts = null) =>
        new(_contacts.Object, Options.Create(opts ?? new TipOptions()), _clock);

    private void Stub(string phone, IReadOnlyDictionary<TipTrigger, DateTimeOffset> cooldowns)
    {
        _contacts.Setup(x => x.GetForTipsAsync(phone, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContactTipState(cooldowns));
    }

    [Fact]
    public async Task PickAsync_DisabledFeature_ReturnsNull()
    {
        Stub("+22070003001", new Dictionary<TipTrigger, DateTimeOffset>());

        var tip = await Build(new TipOptions { Enabled = false })
            .PickAsync("+22070003001", TipTrigger.AfterWelcome);

        tip.ShouldBeNull();
    }

    [Fact]
    public async Task PickAsync_UnknownContact_ReturnsNull()
    {
        _contacts.Setup(x => x.GetForTipsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ContactTipState?)null);

        var tip = await Build().PickAsync("+22070003002", TipTrigger.AfterWelcome);

        tip.ShouldBeNull();
    }

    [Fact]
    public async Task PickAsync_NoCooldownForTrigger_ReturnsTip()
    {
        // Fresh contact: empty cooldown dict → missing trigger maps to DateTimeOffset.MinValue.
        var phone = "+22070003003";
        Stub(phone, new Dictionary<TipTrigger, DateTimeOffset>());

        var tip = await Build().PickAsync(phone, TipTrigger.AfterWelcome);

        tip.ShouldNotBeNull();
        tip!.Trigger.ShouldBe(TipTrigger.AfterWelcome);
    }

    [Fact]
    public async Task PickAsync_WithinCooldownWindow_ReturnsNull()
    {
        var phone = "+22070003004";
        Stub(phone, new Dictionary<TipTrigger, DateTimeOffset>
        {
            [TipTrigger.AfterWelcome] = _clock.GetUtcNow().AddHours(-1)
        });

        var tip = await Build(new TipOptions { DefaultCooldownHours = 24 })
            .PickAsync(phone, TipTrigger.AfterWelcome);

        tip.ShouldBeNull();
    }

    [Fact]
    public async Task PickAsync_DeterministicByPhone()
    {
        var phone = "+22070003005";
        Stub(phone, new Dictionary<TipTrigger, DateTimeOffset>());

        var first = await Build().PickAsync(phone, TipTrigger.AfterWelcome);
        var second = await Build().PickAsync(phone, TipTrigger.AfterWelcome);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        second!.Key.ShouldBe(first!.Key);
    }

    [Fact]
    public async Task PickAsync_CooldownJustElapsed_ReturnsTip()
    {
        var phone = "+22070003006";
        Stub(phone, new Dictionary<TipTrigger, DateTimeOffset>
        {
            [TipTrigger.AfterWelcome] = _clock.GetUtcNow().AddHours(-24).AddSeconds(-1)
        });

        var tip = await Build(new TipOptions { DefaultCooldownHours = 24 })
            .PickAsync(phone, TipTrigger.AfterWelcome);

        tip.ShouldNotBeNull();
    }

    [Fact]
    public async Task PickAsync_OtherTriggerOnCooldown_DoesNotBlockRequestedTrigger()
    {
        // AfterWelcome was just sent — cooldown active for THAT trigger only.
        // UserRequested must still fire: per-trigger isolation is the whole point.
        var phone = "+22070003007";
        Stub(phone, new Dictionary<TipTrigger, DateTimeOffset>
        {
            [TipTrigger.AfterWelcome] = _clock.GetUtcNow().AddMinutes(-5)
        });

        var tip = await Build(new TipOptions { DefaultCooldownHours = 24 })
            .PickAsync(phone, TipTrigger.UserRequested);

        tip.ShouldNotBeNull();
        tip!.Trigger.ShouldBe(TipTrigger.UserRequested);
    }

    [Fact]
    public async Task PickAsync_SameTriggerWithinCooldown_BlocksItself()
    {
        // Mirror of the OtherTrigger test from the opposite angle: the trigger
        // that was just sent IS gated, while a sibling stays open.
        var phone = "+22070003008";
        Stub(phone, new Dictionary<TipTrigger, DateTimeOffset>
        {
            [TipTrigger.AfterWelcome] = _clock.GetUtcNow().AddMinutes(-5)
        });

        var tip = await Build(new TipOptions { DefaultCooldownHours = 24 })
            .PickAsync(phone, TipTrigger.AfterWelcome);

        tip.ShouldBeNull();
    }

    [Fact]
    public async Task PickAsync_PerTriggerCooldownsIndependent_OneElapsedOneActive()
    {
        // AfterWelcome cooldown elapsed, UserRequested still active → only the
        // elapsed-trigger pick succeeds.
        var phone = "+22070003009";
        Stub(phone, new Dictionary<TipTrigger, DateTimeOffset>
        {
            [TipTrigger.AfterWelcome] = _clock.GetUtcNow().AddHours(-25),
            [TipTrigger.UserRequested] = _clock.GetUtcNow().AddMinutes(-10)
        });

        var picker = Build(new TipOptions { DefaultCooldownHours = 24 });

        var elapsed = await picker.PickAsync(phone, TipTrigger.AfterWelcome);
        var stillActive = await picker.PickAsync(phone, TipTrigger.UserRequested);

        elapsed.ShouldNotBeNull();
        stillActive.ShouldBeNull();
    }
}
