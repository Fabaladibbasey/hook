using Hook.Features.Geocoding.GeocodeCache;
using Hook.Features.Geocoding.Models;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests.Geocoding;

[Collection("Pipeline-2")]
public sealed class GeocodeCacheRepositoryTests : PipelineTestBase
{
    public GeocodeCacheRepositoryTests(DevPipelineFixture fx) : base(fx) { }

    private static string UniqueKey() => $"geo:test:{Guid.NewGuid():N}";

    [Fact]
    public async Task SetAsync_NewKey_Inserts()
    {
        var key = UniqueKey();
        var result = new GeocodeResult(
            new Location(13.45, -16.6), "Banjul, Gambia", "test", FromCache: false);

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGeocodeCache>();
            await repo.SetAsync(key, result);
        }

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var loaded = await db.GeocodeCache.AsNoTracking().FirstOrDefaultAsync(e => e.Key == key);
            loaded.ShouldNotBeNull();
            loaded!.FormattedAddress.ShouldBe("Banjul, Gambia");
        }
    }

    [Fact]
    public async Task SetAsync_DuplicateKey_IsSilentNoOp_FirstWriterWins()
    {
        var key = UniqueKey();
        var first = new GeocodeResult(
            new Location(13.45, -16.6), "First", "test", FromCache: false);
        var second = new GeocodeResult(
            new Location(0.0, 0.0), "Second", "test", FromCache: false);

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGeocodeCache>();
            await repo.SetAsync(key, first);
        }

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGeocodeCache>();
            await Should.NotThrowAsync(() => repo.SetAsync(key, second));
        }

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var rows = await db.GeocodeCache.AsNoTracking().Where(e => e.Key == key).ToListAsync();
            rows.Count.ShouldBe(1);
            rows[0].FormattedAddress.ShouldBe("First");
        }
    }
}
