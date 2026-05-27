using Hook.Features.Tips;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.Tips;

public class TipDispatcherTests
{
    private readonly Mock<IMessageBus> _bus = new();
    private readonly Mock<ITipPicker> _picker = new();
    private readonly List<object> _published = [];

    public TipDispatcherTests()
    {
        _bus.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((m, _) => _published.Add(m))
            .Returns(ValueTask.CompletedTask);
        _bus.Setup(x => x.PublishAsync(It.IsAny<RecordTipCooldownCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((m, _) => _published.Add(m))
            .Returns(ValueTask.CompletedTask);
    }

    private TipDispatcher Build() => new(_bus.Object, _picker.Object,
        NullLogger<TipDispatcher>.Instance);

    private static PhoneNumber To() => PhoneNumber.Parse("+22070099001");

    [Fact]
    public async Task DispatchAsync_PickerHits_PublishesPeekedTipBodyDirectly_NoHandlerRePick()
    {
        // Body carries the tip text directly; no Tip rider so the handler will
        // not re-pick. Cooldown rides a follow-up RecordTipCooldownCommand.
        var tip = new Tip("ask:request-or-register", TipTrigger.UserRequested, "Tip: reply REQUEST.");
        _picker.Setup(x => x.PickAsync(It.IsAny<string>(), TipTrigger.UserRequested, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tip);

        await Build().DispatchAsync(To(), CancellationToken.None);

        _published.Count.ShouldBe(2);
        var send = _published[0].ShouldBeOfType<SendWhatsAppTextCommand>();
        send.Text.ShouldBe("Tip: reply REQUEST.");
        send.Tip.ShouldBeNull();
        var record = _published[1].ShouldBeOfType<RecordTipCooldownCommand>();
        record.Trigger.ShouldBe(TipTrigger.UserRequested);
        record.TipKey.ShouldBe("ask:request-or-register");
    }

    [Fact]
    public async Task DispatchAsync_PickerReturnsNull_PublishesFallbackOnly_NoRecordCommand()
    {
        _picker.Setup(x => x.PickAsync(It.IsAny<string>(), TipTrigger.UserRequested, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tip?)null);

        await Build().DispatchAsync(To(), CancellationToken.None);

        _published.Count.ShouldBe(1);
        var cmd = _published[0].ShouldBeOfType<SendWhatsAppTextCommand>();
        cmd.Text.ShouldBe("No new tip right now — try again later.");
        cmd.Tip.ShouldBeNull();
    }

    [Fact]
    public async Task DispatchAsync_PicksAgainstUserRequestedBucket_OnlyOnce()
    {
        _picker.Setup(x => x.PickAsync(It.IsAny<string>(), It.IsAny<TipTrigger>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tip?)null);

        await Build().DispatchAsync(To(), CancellationToken.None);

        _picker.Verify(x => x.PickAsync(It.IsAny<string>(), TipTrigger.UserRequested, It.IsAny<CancellationToken>()),
            Times.Once);
        _picker.Verify(x => x.PickAsync(It.IsAny<string>(), It.IsAny<TipTrigger>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
