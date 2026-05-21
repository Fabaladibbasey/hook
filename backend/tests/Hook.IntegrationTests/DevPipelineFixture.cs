using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using Hook.Features.Ai;
using Hook.Features.ProviderAvailability.Dev;
using Hook.Features.Whatsapp.Dev;
using Hook.Features.Whatsapp.ReceiveWebhook;
using Hook.Shared.Persistence.Data;
using Hook.TestHelpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Polly;
using Polly.Retry;
using Testcontainers.PostgreSql;
using Wolverine;

namespace Hook.IntegrationTests;

public sealed class DevPipelineFixture : IAsyncLifetime
{
    public const double SeedRefLat = 13.4549;
    public const double SeedRefLng = -16.5790;

    // One Postgres container shared across every Pipeline-N shard. Each shard gets
    // its own database cloned from a pre-migrated template, which dodges 3x container
    // startup + 3x migration runs while keeping per-shard DB isolation.
    private const string TemplateDbName = "hook_template";
    private static readonly SemaphoreSlim ContainerInitLock = new(1, 1);
    private static PostgreSqlContainer? _shared;
    private static bool _templateReady;
    private static int _shardCounter;
    private static readonly Regex ShardNameRegex =
        new("^hook_p[0-9]+$", RegexOptions.Compiled);

    // Host startup hits Serilog's static Log.Logger via UseSerilog. With parallel shard
    // fixtures, concurrent host builds race on the global ReloadableLogger and throw
    // "logger is already frozen". Serialize that one-time host build per fixture —
    // shared with HookAppFixture (SmokeTests) which also boots its own host.
    internal static readonly SemaphoreSlim HostInitLock = new(1, 1);

    private string _shardDbName = string.Empty;
    private string? _truncateSql;

    [ModuleInitializer]
    internal static void StaticInit()
    {
        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        {
            if (_shared is { } c)
            {
                try { c.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            }
        };
    }

    public WebApplicationFactory<global::Hook.Program> Factory { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await ContainerInitLock.WaitAsync();
        try
        {
            if (_shared is null)
            {
                _shared = new PostgreSqlBuilder("postgis/postgis:16-3.4")
                    .WithDatabase(TemplateDbName)
                    .WithUsername("hook")
                    .WithPassword("hook")
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5432))
                    .Build();
                await _shared.StartAsync();
            }

            if (!_templateReady)
            {
                var templateConn = _shared.GetConnectionString();
                var opts = new DbContextOptionsBuilder<HookDbContext>()
                    .UseNpgsql(templateConn, npgsql => npgsql.UseNetTopologySuite())
                    .Options;
                await using var ctx = new HookDbContext(opts);
                await ctx.Database.MigrateAsync();
                _templateReady = true;
            }

            var n = Interlocked.Increment(ref _shardCounter);
            _shardDbName = $"hook_p{n}";
            if (!ShardNameRegex.IsMatch(_shardDbName))
                throw new InvalidOperationException($"Refusing to interpolate shard name '{_shardDbName}'.");

            await using var admin = new NpgsqlConnection(BuildAdminConnString(_shared!));
            await admin.OpenAsync();

            // Templates can only be cloned when no other backend has them open.
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT pg_terminate_backend(pid)
                    FROM pg_stat_activity
                    WHERE datname = '{TemplateDbName}' AND pid <> pg_backend_pid();
                    """;
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText = $"CREATE DATABASE \"{_shardDbName}\" TEMPLATE \"{TemplateDbName}\";";
                await cmd.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            ContainerInitLock.Release();
        }

        var refLat = SeedRefLat.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var refLng = SeedRefLng.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var shardConn = new NpgsqlConnectionStringBuilder(_shared!.GetConnectionString())
        {
            Database = _shardDbName,
            MaxPoolSize = 20
        }.ConnectionString;

        Factory = new WebApplicationFactory<global::Hook.Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.UseSetting("ConnectionStrings:HookDb", shardConn);
                b.UseSetting("Whatsapp:VerifyToken", "v");
                b.UseSetting("Whatsapp:AppSecret", "s");
                b.UseSetting("Whatsapp:PhoneNumberId", "PN-1");
                b.UseSetting("Whatsapp:AccessToken", "token");
                b.UseSetting("GoogleGeocoding:ApiKey", "k");
                b.UseSetting("Dev:Whatsapp:Enabled", "true");
                b.UseSetting("Dev:Geocoding:Enabled", "true");
                b.UseSetting("Dev:Providers:Enabled", "true");
                b.UseSetting("Dev:Providers:AutoSeed", "true");
                b.UseSetting("Dev:Providers:ReferenceLat", refLat);
                b.UseSetting("Dev:Providers:ReferenceLng", refLng);
                b.UseSetting("Dev:Providers:TtlHours", "24");
                b.UseSetting("Retention:Enabled", "false");
                // Zero Step1 feedback delay so Wolverine's scheduler dispatches the prompt
                // synchronously (in-memory transport with TimeSpan.Zero); otherwise the
                // +30 min production default would mean the prompt never fires inside a
                // test run. Step2 publishes immediately on Step1=Yes (no separate knob).
                b.UseSetting("Feedback:Step1InitialDelay", "00:00:00");
                b.UseSetting(Shared.Persistence.WolverineConfig.ExecutionTimeoutKey, "10");
                b.ConfigureServices(s =>
                {
                    // Convention-based handler discovery scans Hook.dll only.
                    // Include this test assembly so test-only handlers (e.g.
                    // AutoApplyTransactionsHandler) are picked up at host build,
                    // and force local-queue envelopes to persist in wolverine.*
                    // so EF <-> outbox transactional joins can be asserted.
                    s.AddSingleton<IWolverineExtension, TestPipelineWolverineExtension>();
                });
                b.ConfigureTestServices(s =>
                {
                    s.RemoveAll<IConversationAi>();
                    s.AddSingleton<IConversationAi, FakeConversationAi>();
                });
            });

        // Serialize the first scope creation: that's when WebApplicationFactory builds the
        // host (UseSerilog -> Log.Logger.Freeze() races across shards otherwise).
        await HostInitLock.WaitAsync();
        try
        {
            using var scope = Factory.Services.CreateScope();
            // Schema already migrated in the template — only seed providers.
            var seeder = scope.ServiceProvider.GetRequiredService<DevProviderSeeder>();
            await seeder.SeedAsync();
        }
        finally
        {
            HostInitLock.Release();
        }
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();

        // Static container; the per-shard clone is dropped here. The container itself
        // is reaped by Testcontainers' Ryuk sidecar (enabled by default) on abnormal
        // exit. The ProcessExit hook at the top of this file disposes it on graceful
        // exit and tolerates the "container already gone" race with Ryuk via the
        // outer empty catch.
        if (_shared is not null && !string.IsNullOrEmpty(_shardDbName))
        {
            await using var admin = new NpgsqlConnection(BuildAdminConnString(_shared));
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_shardDbName}\" WITH (FORCE);";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static string BuildAdminConnString(PostgreSqlContainer c)
    {
        var b = new NpgsqlConnectionStringBuilder(c.GetConnectionString()) { Database = "postgres" };
        return b.ConnectionString;
    }

    // Tables whose contents are transactional / per-test state — truncated between
    // tests so the shared collection fixture behaves like a fresh DB per [Fact].
    // Auto-derived from the EF model; reference data ("Services" taxonomy +
    // __EFMigrationsHistory) is preserved. Seeded providers are re-inserted by
    // DevProviderSeeder after the truncate.
    private static readonly HashSet<string> KeepTables = new(StringComparer.Ordinal)
    {
        "services",
        "__EFMigrationsHistory"
    };

    private static IEnumerable<string> AllTransactionalTables(HookDbContext ctx)
    {
        foreach (var e in ctx.Model.GetEntityTypes())
        {
            var name = e.GetTableName();
            if (string.IsNullOrEmpty(name) || KeepTables.Contains(name)) continue;
            var schema = e.GetSchema();
            yield return string.IsNullOrEmpty(schema) ? $"\"{name}\"" : $"\"{schema}\".\"{name}\"";
        }
    }

    private static readonly string[] WolverineTransactionalTables =
    {
        "wolverine.wolverine_incoming_envelopes",
        "wolverine.wolverine_outgoing_envelopes",
        "wolverine.wolverine_dead_letters"
    };

    // Combined TRUNCATE can still 40P01 against the Wolverine durability worker's
    // bookkeeping tx. Bounded retry lets the worker's millisecond-scale tx clear,
    // then TRUNCATE wins on the next attempt.
    private static readonly ResiliencePipeline TruncateRetry = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<PostgresException>(
                ex => ex.SqlState == PostgresErrorCodes.DeadlockDetected),
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(50),
            BackoffType = DelayBackoffType.Exponential,
        })
        .Build();

    public async Task ResetAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        _truncateSql ??= "TRUNCATE TABLE "
            + string.Join(", ", AllTransactionalTables(ctx).Concat(WolverineTransactionalTables).Distinct())
            + " RESTART IDENTITY CASCADE;";

        await TruncateRetry.ExecuteAsync(
            async ct => await ctx.Database.ExecuteSqlRawAsync(_truncateSql, ct));

        var seeder = scope.ServiceProvider.GetRequiredService<DevProviderSeeder>();
        await seeder.SeedAsync();

        scope.ServiceProvider.GetRequiredService<IDevOutbox>().Reset();
        scope.ServiceProvider.GetService<IInboundDedup>()?.Reset();
        (scope.ServiceProvider.GetService<IConversationAi>() as FakeConversationAi)?.ResetOverrides();
    }
}

// Five Pipeline-N standard shards plus a migration-only shard. Each
// [CollectionDefinition] gets its own DevPipelineFixture instance (own database
// cloned from the shared template). xUnit runs collections in parallel
// (xunit.runner.json: parallelizeTestCollections=true); tests within a shard
// remain serialized so TRUNCATE-based per-test isolation still works.
//
// Pipeline-1: ClientRequestPipelineTests, WhatsappContactRepositoryTests
[CollectionDefinition("Pipeline-1")]
public sealed class PipelineCollection1 : ICollectionFixture<DevPipelineFixture> { }

// Pipeline-2: DualRoleTests, FeedbackRepositoryTests, InboundRouterRoutingTests,
// MultiPickPipelineTests, WhatsappInboundDedupPipelineTests
[CollectionDefinition("Pipeline-2")]
public sealed class PipelineCollection2 : ICollectionFixture<DevPipelineFixture> { }

// Pipeline-3: ChatHubTests, MatchIterationPipelineTests,
// ProviderRegistrationFunnelTests, ProviderRegistrationPipelineTests
[CollectionDefinition("Pipeline-3")]
public sealed class PipelineCollection3 : ICollectionFixture<DevPipelineFixture> { }

// Pipeline-4: AmbiguousIntentTests, ChatPrivacyRoutingPipelineTests,
// MatchRepositoryOrderingTests, PhoneExchangerIntegrationTests,
// ProviderQueryStatsTests, RetentionSweeperTests, UniqueIndexTests
[CollectionDefinition("Pipeline-4")]
public sealed class PipelineCollection4 : ICollectionFixture<DevPipelineFixture> { }

// Pipeline-Migration: MigrationRoundTripTests, StaticCodegenSmokeTests,
// DevPipelineFixture_SelfTests, FakeConversationAiOverrideLeakTests.
// Migration round-trip is CPU- and IO-heavy; pin migrations + diagnostic
// self-tests here so they don't drag a slice that holds fast feedback tests.
[CollectionDefinition("Pipeline-Migration")]
public sealed class PipelineMigrationCollection : ICollectionFixture<DevPipelineFixture> { }

// Base for every test class in a Pipeline-N collection: resets transactional DB state
// before each [Fact] so the shared fixture behaves like a fresh DB per test.
public abstract class PipelineTestBase : IAsyncLifetime
{
    protected readonly DevPipelineFixture _fx;
    protected PipelineTestBase(DevPipelineFixture fx) => _fx = fx;

    public Task InitializeAsync() => _fx.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;
}

internal sealed class TestPipelineWolverineExtension : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.Discovery.IncludeAssembly(typeof(DevPipelineFixture).Assembly);
        // Local handlers normally dispatch through an in-memory queue and never
        // touch the outbox. UseDurableLocalQueues forces local-queue envelopes
        // to persist in wolverine.* tables, enabling EF ↔ outbox transactional
        // join assertions in AutoApplyTransactionsTests.
        options.Policies.UseDurableLocalQueues();
    }
}
