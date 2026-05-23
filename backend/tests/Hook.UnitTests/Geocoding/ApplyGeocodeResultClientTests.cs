using Hook.Features.Geocoding.Geocode;
using Hook.Features.Geocoding.Models;
using Hook.Features.ServiceRequest.Create;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.Geocoding;

public sealed class ApplyGeocodeResultClientTests
{
    private const string Phone = "+2207010001";
    private static readonly GeocodeResult Banjul = new(
        new Location(13.4549, -16.5790), "Banjul, The Gambia", "static-dev", FromCache: false);

    private readonly Mock<IClientRequestDraftRepository> _drafts = new();
    private readonly Mock<IMessageBus> _bus = new();
    private readonly List<SendWhatsAppTextRequested> _sent = [];
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    public ApplyGeocodeResultClientTests()
    {
        _bus.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextRequested>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((m, _) => _sent.Add((SendWhatsAppTextRequested)m))
            .Returns(ValueTask.CompletedTask);
        _drafts.Setup(x => x.UpsertAsync(It.IsAny<ClientRequestDraft>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private ApplyGeocodeResultClientHandler Build() =>
        new(_drafts.Object, _clock, NullLogger<ApplyGeocodeResultClientHandler>.Instance);

    private static ClientRequestDraft DraftAt(
        DateTimeOffset stamp,
        ClientRequestStep step = ClientRequestStep.AwaitingLocation)
    {
        var d = ClientRequestDraft.Start(Phone, stamp);
        if (step != ClientRequestStep.AwaitingService)
            d.StepTo(step, stamp);
        return d;
    }

    [Fact]
    public async Task Handle_StaleDraftStamp_DoesNotMutate()
    {
        var oldStamp = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var draft = DraftAt(oldStamp.AddSeconds(15)); // user CANCELed and restarted; new stamp is later.
        _drafts.Setup(x => x.GetAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(draft);

        await Build().Handle(new ApplyGeocodeResultClient(Phone, Banjul, DraftStampedAt: oldStamp),
            _bus.Object, CancellationToken.None);

        draft.Step.ShouldBe(ClientRequestStep.AwaitingLocation);
        draft.DraftLatitude.ShouldBeNull();
        _sent.ShouldBeEmpty();
        _drafts.Verify(x => x.UpsertAsync(It.IsAny<ClientRequestDraft>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_MatchingStamp_AppliesGeocode()
    {
        var stamp = _clock.GetUtcNow();
        var draft = DraftAt(stamp);
        _drafts.Setup(x => x.GetAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(draft);

        await Build().Handle(new ApplyGeocodeResultClient(Phone, Banjul, DraftStampedAt: stamp),
            _bus.Object, CancellationToken.None);

        draft.Step.ShouldBe(ClientRequestStep.ConfirmLocation);
        draft.DraftLatitude.ShouldBe(Banjul.Location.Latitude);
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("Found:");
    }

    [Fact]
    public async Task Handle_LegacyEnvelopeWithDefaultStamp_StillApplies_BackCompat()
    {
        // Envelopes serialised before the schema bump arrive with DraftStampedAt=default.
        // The new guard treats default as "no check requested" so legacy envelopes keep working.
        var stamp = _clock.GetUtcNow();
        var draft = DraftAt(stamp);
        _drafts.Setup(x => x.GetAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(draft);

        await Build().Handle(new ApplyGeocodeResultClient(Phone, Banjul),
            _bus.Object, CancellationToken.None);

        draft.Step.ShouldBe(ClientRequestStep.ConfirmLocation);
        draft.DraftLatitude.ShouldBe(Banjul.Location.Latitude);
    }

    [Fact]
    public async Task Handle_StepAdvancedPastConfirmLocation_DoesNotMutate()
    {
        var stamp = _clock.GetUtcNow();
        var draft = DraftAt(stamp, ClientRequestStep.AwaitingDescription);
        _drafts.Setup(x => x.GetAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(draft);

        await Build().Handle(new ApplyGeocodeResultClient(Phone, Banjul, DraftStampedAt: stamp),
            _bus.Object, CancellationToken.None);

        draft.DraftLatitude.ShouldBeNull();
        _sent.ShouldBeEmpty();
    }
}
