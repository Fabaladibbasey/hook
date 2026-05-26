using Hook.Features.MetaTemplates;
using Hook.Features.Tips;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Hook.UnitTests.Tips;

public class SendWhatsAppTextHandlerTipTests
{
    private readonly Mock<IWhatsappClient> _whatsapp = new();
    private readonly Mock<ITipPicker> _picker = new();
    private readonly Mock<IWhatsappContactRepository> _contacts = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly IOptions<TipOptions> _opts = Options.Create(new TipOptions { Enabled = true });

    private SendWhatsAppTextHandler Build(IOptions<TipOptions>? opts = null) =>
        new(_whatsapp.Object, _picker.Object, _contacts.Object, opts ?? _opts, _clock);

    private static PhoneNumber To() => PhoneNumber.Parse("+22070099001");

    [Fact]
    public async Task Handle_NoTrigger_SendsBodyVerbatim_NoPicker_NoRecord()
    {
        await Build().Handle(new SendWhatsAppTextCommand(To(), "hi there"), CancellationToken.None);

        _whatsapp.Verify(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), "hi there", It.IsAny<CancellationToken>()), Times.Once);
        _picker.Verify(x => x.PickAsync(It.IsAny<string>(), It.IsAny<TipTrigger>(), It.IsAny<CancellationToken>()), Times.Never);
        _contacts.Verify(x => x.RecordTipAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_PickerReturnsTip_AppendsTip_SendBeforeRecord()
    {
        var tip = new Tip("welcome:cancel-anytime", TipTrigger.AfterWelcome, "Tip: cancel anytime.");
        _picker.Setup(x => x.PickAsync(It.IsAny<string>(), TipTrigger.AfterWelcome, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tip);

        string? sentBody = null;
        var sent = false;
        // Capture order: SendTextAsync must fire BEFORE RecordTipAsync so that
        // an HTTP failure aborts the handler without leaving the cooldown set
        // (which would silently drop the tip forever on retry).
        _whatsapp.Setup(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<PhoneNumber, string, CancellationToken>((_, body, _) =>
            {
                sentBody = body;
                sent = true;
            })
            .ReturnsAsync("msg-id");
        _contacts.Setup(x => x.RecordTipAsync(It.IsAny<string>(), tip.Key, _clock.GetUtcNow(), It.IsAny<CancellationToken>()))
            .Callback(() => sent.ShouldBeTrue("SendTextAsync must run BEFORE RecordTipAsync"))
            .Returns(Task.CompletedTask);

        await Build().Handle(
            new SendWhatsAppTextCommand(To(), "Welcome!", Tip: TipTrigger.AfterWelcome),
            CancellationToken.None);

        sentBody.ShouldBe("Welcome!\n\nTip: cancel anytime.");
        _contacts.Verify(x => x.RecordTipAsync(
            It.IsAny<string>(), tip.Key, _clock.GetUtcNow(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_PickerReturnsNull_DoesNotAppend_DoesNotRecord()
    {
        _picker.Setup(x => x.PickAsync(It.IsAny<string>(), It.IsAny<TipTrigger>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tip?)null);

        await Build().Handle(
            new SendWhatsAppTextCommand(To(), "hi", Tip: TipTrigger.AfterWelcome),
            CancellationToken.None);

        _whatsapp.Verify(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), "hi", It.IsAny<CancellationToken>()), Times.Once);
        _contacts.Verify(x => x.RecordTipAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_SendThrows_DoesNotRecordTip()
    {
        var tip = new Tip("welcome:cancel-anytime", TipTrigger.AfterWelcome, "Tip: cancel anytime.");
        _picker.Setup(x => x.PickAsync(It.IsAny<string>(), TipTrigger.AfterWelcome, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tip);
        _whatsapp.Setup(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));

        await Should.ThrowAsync<HttpRequestException>(() => Build().Handle(
            new SendWhatsAppTextCommand(To(), "Welcome!", Tip: TipTrigger.AfterWelcome),
            CancellationToken.None));

        _contacts.Verify(x => x.RecordTipAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_FeatureDisabled_FastExitsBeforePicker()
    {
        var disabled = Options.Create(new TipOptions { Enabled = false });
        await Build(disabled).Handle(
            new SendWhatsAppTextCommand(To(), "hi", Tip: TipTrigger.AfterWelcome),
            CancellationToken.None);

        _picker.Verify(x => x.PickAsync(
            It.IsAny<string>(), It.IsAny<TipTrigger>(), It.IsAny<CancellationToken>()), Times.Never);
        _whatsapp.Verify(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), "hi", It.IsAny<CancellationToken>()), Times.Once);
    }
}
