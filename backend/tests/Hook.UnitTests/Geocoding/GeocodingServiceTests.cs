using Hook.Features.Geocoding;
using Hook.Features.Geocoding.Geocode;
using Hook.Features.Geocoding.GeocodeCache;
using Hook.Features.Geocoding.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Hook.UnitTests.Geocoding;

public class GeocodingServiceTests
{
    [Fact]
    public async Task GeocodeAsync_ShouldReturnCachedResult_WhenKeyExists()
    {
        var cacheMock = new Mock<IGeocodeCache>();
        var cached = new GeocodeResult(new Location(13.4549, -16.5790), "Banjul, The Gambia", "google", FromCache: true);
        cacheMock.Setup(x => x.TryGetAsync("banjul", It.IsAny<CancellationToken>())).ReturnsAsync(cached);
        var geocoderMock = new Mock<IGeocoder>();
        var service = new GeocodingService(geocoderMock.Object, cacheMock.Object, NullLogger<GeocodingService>.Instance);

        var result = await service.GeocodeAsync("Banjul");

        result.ShouldNotBeNull();
        result!.FromCache.ShouldBeTrue();
        geocoderMock.Verify(x => x.GeocodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GeocodeAsync_ShouldFetchAndCache_WhenKeyMissing()
    {
        var store = new Dictionary<string, GeocodeResult>(StringComparer.OrdinalIgnoreCase);
        var cacheMock = new Mock<IGeocodeCache>();
        cacheMock.Setup(x => x.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) =>
                store.TryGetValue(key, out var v)
                    ? new GeocodeResult(v.Location, v.FormattedAddress, v.Provider, FromCache: true)
                    : null);
        cacheMock.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<GeocodeResult>(), It.IsAny<CancellationToken>()))
            .Callback<string, GeocodeResult, CancellationToken>((key, value, _) => store[key] = value)
            .Returns(Task.CompletedTask);

        var geocoderMock = new Mock<IGeocoder>();
        var fetched = new GeocodeResult(new Location(40.7, -74), "New York, NY, USA", "google", FromCache: false);
        geocoderMock.Setup(x => x.GeocodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(fetched);
        var service = new GeocodingService(geocoderMock.Object, cacheMock.Object, NullLogger<GeocodingService>.Instance);

        var first = await service.GeocodeAsync("New York");
        var second = await service.GeocodeAsync("new york");

        first.ShouldNotBeNull();
        first!.FromCache.ShouldBeFalse();
        second.ShouldNotBeNull();
        second!.FromCache.ShouldBeTrue();
        geocoderMock.Verify(x => x.GeocodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GeocodeAsync_ShouldReturnNull_WhenGeocoderReturnsNull()
    {
        var cacheMock = new Mock<IGeocodeCache>();
        cacheMock.Setup(x => x.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GeocodeResult?)null);
        var geocoderMock = new Mock<IGeocoder>();
        geocoderMock.Setup(x => x.GeocodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GeocodeResult?)null);
        var service = new GeocodingService(geocoderMock.Object, cacheMock.Object, NullLogger<GeocodingService>.Instance);

        var result = await service.GeocodeAsync("Atlantis");

        result.ShouldBeNull();
        cacheMock.Verify(x => x.SetAsync(It.IsAny<string>(), It.IsAny<GeocodeResult>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Location_ToPoint_ShouldCarrySrid4326()
    {
        var loc = new Location(13.4549, -16.5790);
        var point = loc.ToPoint();

        point.SRID.ShouldBe(4326);
        point.X.ShouldBe(loc.Longitude, 0.0001);
        point.Y.ShouldBe(loc.Latitude, 0.0001);
    }
}
