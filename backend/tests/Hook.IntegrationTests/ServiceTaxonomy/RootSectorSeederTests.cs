using Hook.Features.ServiceTaxonomy.SeedRoots;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests.ServiceTaxonomy;

[Collection("Pipeline-Migration")]
public sealed class RootSectorSeederTests : PipelineTestBase
{
    public RootSectorSeederTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task EnsureRootSectorsAsync_SeedsAllSixteenRoots_AsRoots()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        // Program.cs ran the seeder on host build — assert post-boot state.
        var roots = await db.Services
            .Where(s => s.ParentSlug == null)
            .Select(s => s.Slug)
            .ToListAsync();

        foreach (var seeded in RootSectorSeeder.RootSlugs)
            roots.ShouldContain(seeded);
    }

    [Fact]
    public async Task EnsureRootSectorsAsync_IsIdempotent_OnReRun()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var seeder = scope.ServiceProvider.GetRequiredService<RootSectorSeeder>();

        await seeder.EnsureRootSectorsAsync();
        var rootCount = await db.Services.CountAsync(s => s.ParentSlug == null);

        // Concurrent test fixtures may have inserted unrelated services so use
        // a >= bound on roots — re-run must not insert duplicates or fail.
        rootCount.ShouldBeGreaterThanOrEqualTo(RootSectorSeeder.RootSlugs.Count);
    }

    [Fact]
    public async Task EnsureRootSectorsAsync_ConcurrentScopes_DoesNotThrow()
    {
        // Two independent scopes race the same PK — TryInsertUniqueAsync must
        // absorb the 23505 from the loser. Exercises the rolling-deploy hot
        // path that the per-row insert pattern is designed to handle.
        await using var scope1 = _fx.Factory.Services.CreateAsyncScope();
        await using var scope2 = _fx.Factory.Services.CreateAsyncScope();
        var seeder1 = scope1.ServiceProvider.GetRequiredService<RootSectorSeeder>();
        var seeder2 = scope2.ServiceProvider.GetRequiredService<RootSectorSeeder>();

        await Task.WhenAll(seeder1.EnsureRootSectorsAsync(), seeder2.EnsureRootSectorsAsync());

        await using var verifyScope = _fx.Factory.Services.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<HookDbContext>();
        var rootCount = await db.Services.CountAsync(s =>
            s.ParentSlug == null && RootSectorSeeder.RootSlugs.Contains(s.Slug));
        rootCount.ShouldBe(RootSectorSeeder.RootSlugs.Count);
    }
}
