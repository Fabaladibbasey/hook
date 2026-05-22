using Hook.Shared.Retention;
using Microsoft.Extensions.Configuration;
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

    [Fact]
    public async Task Bootstrap_TolerantOf_MissingWolverineSchema()
    {
        // Regression: prior diff removed the soft-fail catch, so any failure
        // (including UndefinedTable) would kill host startup. Re-added 42P01
        // catch lets a brand-new schema where Wolverine's own bootstrap hasn't
        // run yet boot anyway; the next host start re-tries idempotently.
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        // NpgsqlDataSource.ConnectionString redacts the password, so we re-source
        // from configuration where the fixture wrote the live connection string
        // including credentials.
        var configConn = scope.ServiceProvider.GetRequiredService<IConfiguration>()
            .GetConnectionString("HookDb") ?? throw new InvalidOperationException("HookDb conn string missing");

        var connBuilder = new NpgsqlConnectionStringBuilder(configConn)
        {
            Database = "postgres"
        };
        var adminConn = connBuilder.ConnectionString;
        var tempDb = $"hook_dlq_bootstrap_{Guid.NewGuid():N}"[..28];

        await using (var admin = new NpgsqlConnection(adminConn))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{tempDb}\";";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var tempBuilder = new NpgsqlConnectionStringBuilder(configConn)
            {
                Database = tempDb
            };
            await using var tempDs = NpgsqlDataSource.Create(tempBuilder.ConnectionString);
            var bootstrap = new WolverineDlqIndexBootstrap(
                tempDs, NullLogger<WolverineDlqIndexBootstrap>.Instance);

            await Should.NotThrowAsync(() => bootstrap.StartAsync(default));
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConn);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{tempDb}\" WITH (FORCE);";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
