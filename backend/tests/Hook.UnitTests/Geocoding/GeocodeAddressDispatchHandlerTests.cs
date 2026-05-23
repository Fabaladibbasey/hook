using Hook.Features.Geocoding;
using Hook.Features.Geocoding.Geocode;
using Hook.Features.Geocoding.GeocodeCache;
using Hook.Features.Geocoding.Models;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.Geocoding;

public class GeocodeAddressDispatchHandlerTests
{
    private readonly Mock<IGeocoder> _geocoderMock = new();
    private readonly Mock<IGeocodeCache> _cacheMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly List<SendWhatsAppTextCommand> _sent = [];
    private readonly List<object> _invoked = [];

    public GeocodeAddressDispatchHandlerTests()
    {
        _cacheMock.Setup(x => x.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GeocodeResult?)null);
        _cacheMock.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<GeocodeResult>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _busMock.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((m, _) => _sent.Add((SendWhatsAppTextCommand)m))
            .Returns(ValueTask.CompletedTask);
        _busMock.Setup(x => x.PublishAsync(It.IsAny<ApplyClientGeocodeResultCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions?>((m, _) => _invoked.Add(m))
            .Returns(ValueTask.CompletedTask);
        _busMock.Setup(x => x.PublishAsync(It.IsAny<ApplyProviderGeocodeResultCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions?>((m, _) => _invoked.Add(m))
            .Returns(ValueTask.CompletedTask);
    }

    private GeocodeAddressDispatchHandler Build() =>
        new(new GeocodingService(_geocoderMock.Object, _cacheMock.Object, NullLogger<GeocodingService>.Instance),
            NullLogger<GeocodeAddressDispatchHandler>.Instance);

    [Fact]
    public async Task Handle_GeocoderReturnsNull_SendsFallbackAndDoesNotInvokeApply()
    {
        _geocoderMock.Setup(x => x.GeocodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GeocodeResult?)null);

        await Build().Handle(
            new GeocodeAddressCommand("+2207000001", "nowhere land", GeocodeFlow.Client),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("couldn't find that address");
        _invoked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_GeocodeSucceeds_ClientFlow_InvokesApplyClient()
    {
        var result = new GeocodeResult(new Location(13.4549, -16.5790), "Banjul, The Gambia", "static-dev", FromCache: false);
        _geocoderMock.Setup(x => x.GeocodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        await Build().Handle(
            new GeocodeAddressCommand("+2207000001", "Banjul", GeocodeFlow.Client),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _invoked.ShouldHaveSingleItem();
        var apply = _invoked[0].ShouldBeOfType<ApplyClientGeocodeResultCommand>();
        apply.Phone.ShouldBe("+2207000001");
        apply.Result.FormattedAddress.ShouldBe("Banjul, The Gambia");
    }

    [Fact]
    public async Task Handle_GeocodeSucceeds_ProviderFlow_InvokesApplyProvider()
    {
        var result = new GeocodeResult(new Location(13.4549, -16.5790), "Bakau, The Gambia", "static-dev", FromCache: false);
        _geocoderMock.Setup(x => x.GeocodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        await Build().Handle(
            new GeocodeAddressCommand("+2207000002", "Bakau Newtown", GeocodeFlow.Provider),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _invoked.ShouldHaveSingleItem();
        var apply = _invoked[0].ShouldBeOfType<ApplyProviderGeocodeResultCommand>();
        apply.Phone.ShouldBe("+2207000002");
        apply.Result.FormattedAddress.ShouldBe("Bakau, The Gambia");
    }

    [Fact]
    public async Task Handle_GeocodeSucceeds_ForwardsDraftStampedAt_OnApplyEnvelope()
    {
        var result = new GeocodeResult(new Location(13.4549, -16.5790), "Banjul, The Gambia", "static-dev", FromCache: false);
        _geocoderMock.Setup(x => x.GeocodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        var stamp = DateTimeOffset.UtcNow;

        await Build().Handle(
            new GeocodeAddressCommand("+2207000001", "Banjul", GeocodeFlow.Client, DraftStampedAt: stamp),
            _busMock.Object, CancellationToken.None);

        var apply = _invoked[0].ShouldBeOfType<ApplyClientGeocodeResultCommand>();
        apply.DraftStampedAt.ShouldBe(stamp);
    }

    [Fact]
    public async Task Handle_UnparseablePhone_NoOp()
    {
        await Build().Handle(
            new GeocodeAddressCommand("not-a-phone", "Banjul", GeocodeFlow.Client),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _invoked.ShouldBeEmpty();
        _geocoderMock.Verify(x => x.GeocodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
