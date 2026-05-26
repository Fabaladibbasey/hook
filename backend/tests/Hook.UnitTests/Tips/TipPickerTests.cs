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

    private void Stub(string phone, DateTimeOffset lastTipAt, string lastTipKey = "")
    {
        _contacts.Setup(x => x.GetForTipsAsync(phone, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContactTipState(lastTipKey, lastTipAt));
    }

    [Fact]
    public async Task PickAsync_DisabledFeature_ReturnsNull()
    {
        Stub("+22070003001", DateTimeOffset.MinValue);

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
    public async Task PickAsync_EpochLastTip_ReturnsTip()
    {
        // Fresh contact: LastTipAt = epoch sentinel, cooldown trivially elapsed.
        var phone = "+22070003003";
        Stub(phone, new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var tip = await Build().PickAsync(phone, TipTrigger.AfterWelcome);

        tip.ShouldNotBeNull();
        tip!.Trigger.ShouldBe(TipTrigger.AfterWelcome);
    }

    [Fact]
    public async Task PickAsync_WithinCooldownWindow_ReturnsNull()
    {
        var phone = "+22070003004";
        Stub(phone, _clock.GetUtcNow().AddHours(-1));

        var tip = await Build(new TipOptions { DefaultCooldownHours = 24 })
            .PickAsync(phone, TipTrigger.AfterWelcome);

        tip.ShouldBeNull();
    }

    [Fact]
    public async Task PickAsync_DeterministicByPhone()
    {
        var phone = "+22070003005";
        Stub(phone, DateTimeOffset.UnixEpoch);

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
        Stub(phone, _clock.GetUtcNow().AddHours(-24).AddSeconds(-1));

        var tip = await Build(new TipOptions { DefaultCooldownHours = 24 })
            .PickAsync(phone, TipTrigger.AfterWelcome);

        tip.ShouldNotBeNull();
    }
}
