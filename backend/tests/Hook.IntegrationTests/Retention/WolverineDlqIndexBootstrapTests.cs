using Hook.Shared.Retention;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;

namespace Hook.IntegrationTests.Retention;

[Collection("Pipeline-4")]
public sealed class WolverineDlqIndexBootstrapTests : PipelineTestBase
{
    public WolverineDlqIndexBootstrapTests(DevPipelineFixture fx) : base(fx) { }

    private static async Task<bool> IndexExistsAsync(NpgsqlDataSource ds, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM pg_indexes WHERE schemaname = 'wolverine' AND indexname = @name)",
            conn);
        cmd.Parameters.AddWithValue("name", WolverineDlqIndexBootstrap.IndexName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return (bool)(result ?? false);
    }

    [Fact]
    public async Task StartAsync_AfterHostBoot_IndexExists()
    {
        // The bootstrap is registered as IHostedService by AddRetentionSweeper, so
        // it has already run during the DevPipelineFixture host startup.
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var ds = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        (await IndexExistsAsync(ds)).ShouldBeTrue();
    }

    [Fact]
    public async Task StartAsync_RunTwice_IsIdempotent()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var ds = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var bootstrap = new WolverineDlqIndexBootstrap(
            ds, NullLogger<WolverineDlqIndexBootstrap>.Instance);

        await bootstrap.StartAsync(default);
        await bootstrap.StartAsync(default);

        (await IndexExistsAsync(ds)).ShouldBeTrue();
    }
}
