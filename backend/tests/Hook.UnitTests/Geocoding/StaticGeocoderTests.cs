using Hook.Features.Geocoding;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Hook.UnitTests.Geocoding;

public class StaticGeocoderTests
{
    [Fact]
    public async Task GeocodeAsync_ShouldHonorConfiguredDefaultCoordinates()
    {
        var opts = Options.Create(new DevGeocodingOptions
        {
            DefaultLat = 13.4549,
            DefaultLng = -16.5790
        });
        var geocoder = new StaticGeocoder(opts);

        var result = await geocoder.GeocodeAsync("Independence Drive, Banjul");

        result.ShouldNotBeNull();
        result!.Location.Latitude.ShouldBe(13.4549);
        result.Location.Longitude.ShouldBe(-16.5790, 0.0001);
        result.FormattedAddress.ShouldBe("Independence Drive, Banjul");
        result.Provider.ShouldBe("static-dev");
    }

    [Fact]
    public async Task GeocodeAsync_ShouldFallBackToBanjulDefaults_WhenOptionsUnset()
    {
        var opts = Options.Create(new DevGeocodingOptions());
        var geocoder = new StaticGeocoder(opts);

        var result = await geocoder.GeocodeAsync("anywhere");

        result.ShouldNotBeNull();
        result!.Location.Latitude.ShouldBe(13.4549);
        result.Location.Longitude.ShouldBe(-16.5790);
    }
}
