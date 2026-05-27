using System.Diagnostics.Metrics;
using Hook.Features.MetaTemplates;
using Hook.Features.Observability;
using Hook.Features.Tips;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
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
        new(_whatsapp.Object, _picker.Object, _contacts.Object, opts ?? _opts, _clock,
            NullLogger<SendWhatsAppTextHandler>.Instance);

    private static PhoneNumber To() => PhoneNumber.Parse("+22070099001");

    [Fact]
    public async Task Handle_NoTrigger_SendsBodyVerbatim_NoPicker_NoRecord()
    {
        await Build().Handle(new SendWhatsAppTextCommand(To(), "hi there"), CancellationToken.None);

        _whatsapp.Verify(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), "hi there", It.IsAny<CancellationToken>()), Times.Once);
        _picker.Verify(x => x.PickAsync(It.IsAny<string>(), It.IsAny<TipTrigger>(), It.IsAny<CancellationToken>()), Times.Never);
        _contacts.Verify(x => x.RecordTipAsync(
            It.IsAny<string>(), It.IsAny<TipTrigger>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _contacts.Setup(x => x.RecordTipAsync(It.IsAny<string>(), TipTrigger.AfterWelcome, _clock.GetUtcNow(), It.IsAny<CancellationToken>()))
            .Callback(() => sent.ShouldBeTrue("SendTextAsync must run BEFORE RecordTipAsync"))
            .Returns(Task.CompletedTask);

        await Build().Handle(
            new SendWhatsAppTextCommand(To(), "Welcome!", Tip: TipTrigger.AfterWelcome),
            CancellationToken.None);

        sentBody.ShouldBe("Welcome!\n\nTip: cancel anytime.");
        _contacts.Verify(x => x.RecordTipAsync(
            It.IsAny<string>(), TipTrigger.AfterWelcome, _clock.GetUtcNow(), It.IsAny<CancellationToken>()), Times.Once);
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
            It.IsAny<string>(), It.IsAny<TipTrigger>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
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
            It.IsAny<string>(), It.IsAny<TipTrigger>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmptyBodyWithTipRider_PickerHits_SendsTipAsBodyWithoutSeparator()
    {
        var tip = new Tip("ask:request-or-register", TipTrigger.UserRequested, "Tip: reply REQUEST.");
        _picker.Setup(x => x.PickAsync(It.IsAny<string>(), TipTrigger.UserRequested, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tip);
        string? sentBody = null;
        _whatsapp.Setup(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<PhoneNumber, string, CancellationToken>((_, body, _) => sentBody = body)
            .ReturnsAsync("msg-id");

        await Build().Handle(
            new SendWhatsAppTextCommand(To(), string.Empty, Tip: TipTrigger.UserRequested),
            CancellationToken.None);

        sentBody.ShouldBe("Tip: reply REQUEST.");
    }

    [Fact]
    public async Task Handle_EmptyBodyWithTipRider_PickerRacedToNull_ShortCircuits_NoSend_NoRecord()
    {
        // TipDispatcher peeked OK but a concurrent dispatch set the cooldown
        // before the handler's pick — handler must not ship an empty WhatsApp
        // message.
        _picker.Setup(x => x.PickAsync(It.IsAny<string>(), TipTrigger.UserRequested, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tip?)null);

        await Build().Handle(
            new SendWhatsAppTextCommand(To(), string.Empty, Tip: TipTrigger.UserRequested),
            CancellationToken.None);

        _whatsapp.Verify(x => x.SendTextAsync(
            It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _contacts.Verify(x => x.RecordTipAsync(
            It.IsAny<string>(), It.IsAny<TipTrigger>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_SendThrowsThenRetrySucceeds_TipReDeliveredOnSecondAttempt()
    {
        // Locks in the documented send-first / persist-cooldown-on-success ordering:
        // a transient HTTP failure leaves the cooldown unset so a Wolverine retry
        // re-delivers the tip rather than silently dropping it forever. A future
        // "fix the duplicate" refactor that persists-then-sends would skip the tip
        // on retry and fail this test.
        var tip = new Tip("welcome:cancel-anytime", TipTrigger.AfterWelcome, "Tip: cancel anytime.");
        _picker.Setup(x => x.PickAsync(It.IsAny<string>(), TipTrigger.AfterWelcome, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tip);

        var sentBodies = new List<string>();
        var callCount = 0;
        _whatsapp.Setup(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<PhoneNumber, string, CancellationToken>((_, body, _) =>
            {
                callCount++;
                sentBodies.Add(body);
                if (callCount == 1) throw new HttpRequestException("boom");
                return Task.FromResult("msg-id");
            });

        var handler = Build();
        var cmd = new SendWhatsAppTextCommand(To(), "Welcome!", Tip: TipTrigger.AfterWelcome);

        await Should.ThrowAsync<HttpRequestException>(() => handler.Handle(cmd, CancellationToken.None));
        _contacts.Verify(x => x.RecordTipAsync(
            It.IsAny<string>(), It.IsAny<TipTrigger>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await handler.Handle(cmd, CancellationToken.None);

        _whatsapp.Verify(x => x.SendTextAsync(
            It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        sentBodies.Count.ShouldBe(2);
        sentBodies[0].ShouldBe("Welcome!\n\nTip: cancel anytime.");
        sentBodies[1].ShouldBe("Welcome!\n\nTip: cancel anytime.");
        _contacts.Verify(x => x.RecordTipAsync(
            It.IsAny<string>(), TipTrigger.AfterWelcome, _clock.GetUtcNow(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_RecordTipThrows_DoesNotFaultHandler_SendStillCompletes_AndIncrementsMetric()
    {
        var tip = new Tip("welcome:cancel-anytime", TipTrigger.AfterWelcome, "Tip: cancel anytime.");
        _picker.Setup(x => x.PickAsync(It.IsAny<string>(), TipTrigger.AfterWelcome, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tip);
        _whatsapp.Setup(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("msg-id");
        _contacts.Setup(x => x.RecordTipAsync(
                It.IsAny<string>(), It.IsAny<TipTrigger>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db blip"));

        var captured = new List<(long Value, string TipKey)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == HookMetrics.MeterName
                && instrument.Name == "hook.tip.cooldown_persist.failures")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            string tipKey = string.Empty;
            foreach (var kv in tags)
                if (kv.Key == "tip_key") tipKey = kv.Value?.ToString() ?? string.Empty;
            captured.Add((measurement, tipKey));
        });
        listener.Start();

        // Must NOT throw — a Wolverine retry would re-send the WhatsApp text.
        await Build().Handle(
            new SendWhatsAppTextCommand(To(), "Welcome!", Tip: TipTrigger.AfterWelcome),
            CancellationToken.None);

        _whatsapp.Verify(x => x.SendTextAsync(
            It.IsAny<PhoneNumber>(), "Welcome!\n\nTip: cancel anytime.", It.IsAny<CancellationToken>()),
            Times.Once);
        captured.ShouldContain(m => m.TipKey == tip.Key);
    }

    [Fact]
    public async Task Handle_EmptyBodyWithoutTipRider_ShipsToWhatsAppLayer()
    {
        // Empty body without a Tip rider is an upstream bug — pass it through
        // so the WhatsApp HTTP layer (or its logs) surface it rather than
        // silently dropping. Only empty body + Tip rider should short-circuit.
        string? sentBody = null;
        _whatsapp.Setup(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<PhoneNumber, string, CancellationToken>((_, body, _) => sentBody = body)
            .ReturnsAsync("msg-id");

        await Build().Handle(
            new SendWhatsAppTextCommand(To(), string.Empty),
            CancellationToken.None);

        sentBody.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Handle_RecordTipThrowsOce_DoesNotFaultHandler_SendStillCompletes()
    {
        // OCE from RecordTipAsync must not escape — Wolverine treats it as a
        // transient fault and retries the envelope, re-sending the WhatsApp
        // text. The previous `when (ex is not OCE)` filter let it escape.
        var tip = new Tip("welcome:cancel-anytime", TipTrigger.AfterWelcome, "Tip: cancel anytime.");
        _picker.Setup(x => x.PickAsync(It.IsAny<string>(), TipTrigger.AfterWelcome, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tip);
        _whatsapp.Setup(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("msg-id");
        _contacts.Setup(x => x.RecordTipAsync(
                It.IsAny<string>(), It.IsAny<TipTrigger>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Build().Handle(
            new SendWhatsAppTextCommand(To(), "Welcome!", Tip: TipTrigger.AfterWelcome),
            CancellationToken.None);

        _whatsapp.Verify(x => x.SendTextAsync(
            It.IsAny<PhoneNumber>(), "Welcome!\n\nTip: cancel anytime.", It.IsAny<CancellationToken>()),
            Times.Once);
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
