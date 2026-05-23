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

public sealed class ApplyProviderGeocodeResultHandlerTests
{
    private const string Phone = "+2207020001";
    private static readonly GeocodeResult Bakau = new(
        new Location(13.4795, -16.6816), "Bakau, The Gambia", "static-dev", FromCache: false);

    private readonly Mock<IRegistrationDraftRepository> _drafts = new();
    private readonly Mock<IMessageBus> _bus = new();
    private readonly List<SendWhatsAppTextCommand> _sent = [];
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    public ApplyProviderGeocodeResultHandlerTests()
    {
        _bus.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((m, _) => _sent.Add((SendWhatsAppTextCommand)m))
            .Returns(ValueTask.CompletedTask);
        _drafts.Setup(x => x.UpsertAsync(It.IsAny<RegistrationDraft>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private ApplyProviderGeocodeResultHandler Build() =>
        new(_drafts.Object, _clock, NullLogger<ApplyProviderGeocodeResultHandler>.Instance);

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
        var oldStamp = _clock.GetUtcNow().AddDays(-30);
        var draft = DraftAt(oldStamp.AddSeconds(15));
        _drafts.Setup(x => x.GetAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(draft);

        await Build().Handle(new ApplyProviderGeocodeResultCommand(Phone, Bakau, DraftStampedAt: oldStamp),
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

        await Build().Handle(new ApplyProviderGeocodeResultCommand(Phone, Bakau, DraftStampedAt: stamp),
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

        await Build().Handle(new ApplyProviderGeocodeResultCommand(Phone, Bakau),
            _bus.Object, CancellationToken.None);

        draft.Step.ShouldBe(RegistrationStep.ConfirmLocation);
    }
}
