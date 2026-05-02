using Hook.Features.Geocoding;
using Hook.Features.Geocoding.Geocode;
using Hook.Features.Geocoding.GeocodeCache;
using Hook.Features.Geocoding.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Hook.UnitTests.Geocoding;

public class GeocodingServiceTests
{
    [Fact]
    public async Task GeocodeAsync_ShouldReturnCachedResult_WhenKeyExists()
    {
        var cache = new FakeCache();
        cache.Seed("banjul", new GeocodeResult(new Location(13.4549, -16.5790), "Banjul, The Gambia", "google", FromCache: true));
        var geocoder = new ScriptedGeocoder();
        var service = new GeocodingService(geocoder, cache, NullLogger<GeocodingService>.Instance);

        var result = await service.GeocodeAsync("Banjul");

        result.ShouldNotBeNull();
        result!.FromCache.ShouldBeTrue();
        geocoder.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GeocodeAsync_ShouldFetchAndCache_WhenKeyMissing()
    {
        var cache = new FakeCache();
        var geocoder = new ScriptedGeocoder
        {
            Result = new GeocodeResult(new Location(40.7, -74), "New York, NY, USA", "google", FromCache: false)
        };
        var service = new GeocodingService(geocoder, cache, NullLogger<GeocodingService>.Instance);

        var first = await service.GeocodeAsync("New York");
        var second = await service.GeocodeAsync("new york");

        first.ShouldNotBeNull();
        first!.FromCache.ShouldBeFalse();
        second.ShouldNotBeNull();
        second!.FromCache.ShouldBeTrue();
        geocoder.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task GeocodeAsync_ShouldReturnNull_WhenGeocoderReturnsNull()
    {
        var cache = new FakeCache();
        var geocoder = new ScriptedGeocoder { Result = null };
        var service = new GeocodingService(geocoder, cache, NullLogger<GeocodingService>.Instance);

        var result = await service.GeocodeAsync("Atlantis");

        result.ShouldBeNull();
        cache.Stored.ShouldBeEmpty();
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

    private sealed class FakeCache : IGeocodeCache
    {
        private readonly Dictionary<string, GeocodeResult> _store = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, GeocodeResult> Stored => _store;

        public void Seed(string key, GeocodeResult result) => _store[key] = result;

        public Task<GeocodeResult?> TryGetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_store.TryGetValue(key, out var v)
                ? new GeocodeResult(v.Location, v.FormattedAddress, v.Provider, FromCache: true)
                : null);

        public Task SetAsync(string key, GeocodeResult result, CancellationToken ct = default)
        {
            _store[key] = result;
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedGeocoder : IGeocoder
    {
        public GeocodeResult? Result { get; set; }
        public int Calls { get; private set; }

        public Task<GeocodeResult?> GeocodeAsync(string address, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }
}
