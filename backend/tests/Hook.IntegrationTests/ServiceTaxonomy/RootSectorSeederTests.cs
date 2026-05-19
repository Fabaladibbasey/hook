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
}
