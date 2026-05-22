using Hook.Features.Geocoding.Geocode;
using Hook.Features.Geocoding.Models;
using Hook.Features.ProviderAvailability.Register;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.Geocoding;

public sealed class ApplyGeocodeResultProviderTests
{
    private const string Phone = "+2207020001";
    private static readonly GeocodeResult Bakau = new(
        new Location(13.4795, -16.6816), "Bakau, The Gambia", "static-dev", FromCache: false);

    private readonly Mock<IRegistrationDraftRepository> _drafts = new();
    private readonly Mock<IMessageBus> _bus = new();
    private readonly List<SendWhatsAppTextRequested> _sent = [];
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    public ApplyGeocodeResultProviderTests()
    {
        _bus.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextRequested>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((m, _) => _sent.Add((SendWhatsAppTextRequested)m))
            .Returns(ValueTask.CompletedTask);
        _drafts.Setup(x => x.UpsertAsync(It.IsAny<RegistrationDraft>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private ApplyGeocodeResultProviderHandler Build() =>
        new(_drafts.Object, _clock, NullLogger<ApplyGeocodeResultProviderHandler>.Instance);

    private static RegistrationDraft DraftAt(
        DateTimeOffset stamp,
        RegistrationStep step = RegistrationStep.AwaitingLocation)
    {
        var d = RegistrationDraft.Start(Phone, stamp);
        if (step != RegistrationStep.AwaitingServices)
            d.StepTo(step, stamp);
        return d;
    }

    [Fact]
    public async Task Handle_StaleDraftStamp_DoesNotMutate()
    {
        var oldStamp = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var draft = DraftAt(oldStamp.AddSeconds(15));
        _drafts.Setup(x => x.GetAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(draft);

        await Build().Handle(new ApplyGeocodeResultProvider(Phone, Bakau, DraftStampedAt: oldStamp),
            _bus.Object, CancellationToken.None);

        draft.Step.ShouldBe(RegistrationStep.AwaitingLocation);
        draft.DraftLatitude.ShouldBeNull();
        _sent.ShouldBeEmpty();
        _drafts.Verify(x => x.UpsertAsync(It.IsAny<RegistrationDraft>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_MatchingStamp_AppliesGeocode()
    {
        var stamp = _clock.GetUtcNow();
        var draft = DraftAt(stamp);
        _drafts.Setup(x => x.GetAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(draft);

        await Build().Handle(new ApplyGeocodeResultProvider(Phone, Bakau, DraftStampedAt: stamp),
            _bus.Object, CancellationToken.None);

        draft.Step.ShouldBe(RegistrationStep.ConfirmLocation);
        draft.DraftLatitude.ShouldBe(Bakau.Location.Latitude);
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("Found:");
    }

    [Fact]
    public async Task Handle_LegacyEnvelopeWithDefaultStamp_StillApplies_BackCompat()
    {
        var stamp = _clock.GetUtcNow();
        var draft = DraftAt(stamp);
        _drafts.Setup(x => x.GetAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(draft);

        await Build().Handle(new ApplyGeocodeResultProvider(Phone, Bakau),
            _bus.Object, CancellationToken.None);

        draft.Step.ShouldBe(RegistrationStep.ConfirmLocation);
    }
}
