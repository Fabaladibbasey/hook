using Hook.Features.Geocoding.GeocodeCache;
using Hook.Features.MetaTemplates;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace Hook.IntegrationTests;

[Collection("Pipeline-Migration")]
public sealed class DevPipelineFixture_SelfTests : PipelineTestBase
{
    public DevPipelineFixture_SelfTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task ResetAsync_TruncatesEveryTransactionalTable_AndKeepsReferenceData()
    {
        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            db.WhatsappContacts.Add(new WhatsappContact { Phone = "+22030099999", LastInboundAt = DateTimeOffset.UtcNow });
            db.GeocodeCache.Add(new GeocodeCacheEntry
            {
                Key = "self-test-key",
                Latitude = 1,
                Longitude = 2,
                FormattedAddress = "x",
                Provider = "test",
                FetchedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await _fx.ResetAsync();

        await using var verify = _fx.Factory.Services.CreateAsyncScope();
        var ctx = verify.ServiceProvider.GetRequiredService<HookDbContext>();

        var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
        await conn.OpenAsync();
        foreach (var entity in ctx.Model.GetEntityTypes())
        {
            var name = entity.GetTableName();
            // Reference taxonomy + migration history are kept; provider_availabilities
            // is truncated by Reset but immediately re-seeded by DevProviderSeeder.
            if (string.IsNullOrEmpty(name)
                || name is "services" or "__EFMigrationsHistory" or "provider_availabilities")
            {
                continue;
            }
            var schema = entity.GetSchema();
            var qualified = string.IsNullOrEmpty(schema) ? $"\"{name}\"" : $"\"{schema}\".\"{name}\"";
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {qualified}";
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            count.ShouldBe(0, $"Reset left rows in {qualified}");
        }

        // ProviderAvailabilities is truncated then re-seeded by DevProviderSeeder.
        (await ctx.ProviderAvailabilities.CountAsync()).ShouldBeGreaterThan(0);
        await using var migrationsCmd = conn.CreateCommand();
        migrationsCmd.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\"";
        var migrations = (long)(await migrationsCmd.ExecuteScalarAsync())!;
        migrations.ShouldBeGreaterThan(0);
    }
}
