using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hook.IntegrationTests.Migrations;

[Collection("Pipeline-Migration")]
public sealed class MigrationRoundTripTests : PipelineTestBase
{
    public MigrationRoundTripTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task AllMigrations_AppliedAfterFixtureInit_NoPendingMigrations()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        var pending = await db.Database.GetPendingMigrationsAsync();
        Assert.Empty(pending);

        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.NotEmpty(applied);

        // The two retention-related migrations from this PR are applied:
        Assert.Contains(applied, m => m.EndsWith("_AddRetentionCascades"));
        Assert.Contains(applied, m => m.EndsWith("_AddSweepIndexesAndCascades"));
    }

    [Fact]
    public async Task MigrateAsync_RunTwice_IsNoOp()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        // First MigrateAsync ran in DevPipelineFixture.InitializeAsync. Calling it again
        // must be safe: no exceptions, no new rows in __EFMigrationsHistory.
        var before = (await db.Database.GetAppliedMigrationsAsync()).Count();
        await db.Database.MigrateAsync();
        var after = (await db.Database.GetAppliedMigrationsAsync()).Count();

        Assert.Equal(before, after);
    }
}
