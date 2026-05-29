using Hook;
using Hook.Features.Ai;
using Hook.Features.Ai.Warmup;
using Hook.Features.ChatLifecycle;
using Hook.Features.ChatPrivacyRouting;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.OpenChat;
using Hook.Features.ContactSharing;
using Hook.Features.Feedback;
using Hook.Features.Geocoding;
using Hook.Features.Matching;
using Hook.Features.MetaTemplates;
using Hook.Features.Observability;
using Hook.Features.ProviderAvailability;
using Hook.Features.ProviderAvailability.Dev;
using Hook.Features.RateLimiting;
using Hook.Features.ServiceRequest;
using Hook.Features.ServiceTaxonomy;
using Hook.Features.ServiceTaxonomy.SeedRoots;
using Hook.Features.Tips;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Dev;
using Hook.Features.Whatsapp.ReceiveWebhook;
using Hook.Shared.Core;
using Hook.Shared.Domain;
using Hook.Shared.Messaging;
using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;
using Hook.Shared.Retention;
using Hook.Shared.Security;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serilog;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;

// Bootstrap logger captured at process start; UseSerilog freezes it on first host build.
// Test shards serialize the first host build (DevPipelineFixture.HostInitLock) to avoid
// concurrent UseSerilog frozen-logger races.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    FrontendBootstrapper.EnsureBuilt(builder.Environment);

    builder.Host.UseSerilog((context, services, config) => config
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    var connectionString = builder.Configuration.GetConnectionString("HookDb")
        ?? throw new InvalidOperationException("Connection string 'HookDb' is not configured.");

    // Single NpgsqlDataSource shared by EF (scoped + factory) and Wolverine so
    // pool bounds + NetTopologySuite mapping apply uniformly. Default pool min=0
    // makes cold-start pay connection-establishment for the first concurrent
    // burst — pin min=5 / max=50 (matches RateLimit:WebhookConcurrencyLimit) so
    // the pool is sized for steady-state and a saturated webhook limiter does
    // not queue on connection establishment. Defaults are only applied when the
    // caller has not pinned them explicitly in the connection string, so test
    // shards that scope down (MaxPoolSize=20 per DevPipelineFixture) retain
    // their override.
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    if (!dataSourceBuilder.ConnectionStringBuilder.ContainsKey("Minimum Pool Size"))
        dataSourceBuilder.ConnectionStringBuilder.MinPoolSize = 5;
    if (!dataSourceBuilder.ConnectionStringBuilder.ContainsKey("Maximum Pool Size"))
        // RateLimit:WebhookConcurrencyLimit (50) + 14 headroom for Wolverine outbox
        // pollers + ambient EF factory reads. See Features/RateLimiting/README.md.
        dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = 64;
    dataSourceBuilder.UseNetTopologySuite();
    var dataSource = dataSourceBuilder.Build();
    builder.Services.AddSingleton(dataSource);

    // AddDbContextWithWolverineIntegration installs a model customizer that
    // requires Wolverine.RDBMS.DatabaseSettings in DI — paired with
    // PersistMessagesWithPostgresql in the UseWolverine block so every
    // environment (including tests) gets the EF transactional outbox.
    // Per-test isolation relies on each fixture using its own Postgres database.
    // The EF-side npgsql.UseNetTopologySuite() registers the EF model
    // mapping for Point -> geography(Point,4326); the data-source-side
    // UseNetTopologySuite registers the underlying Npgsql type info resolver.
    builder.Services.AddDbContextWithWolverineIntegration<HookDbContext>(options =>
        options.UseNpgsql(dataSource, npgsql =>
        {
            npgsql.UseNetTopologySuite();
            npgsql.MigrationsAssembly(typeof(HookDbContext).Assembly.GetName().Name);
        }));

    // Factory for read-only parallel reads (PostgresProviderQueryService branch
    // fan-in, SlugResolver.ResolveBatchAsync isolated paths) — the scoped
    // HookDbContext is reserved for tracked entities + the Wolverine handler tx
    // and can't run concurrent operations.
    builder.Services.AddDbContextFactory<HookDbContext>(options =>
        options.UseNpgsql(dataSource, npgsql =>
        {
            npgsql.UseNetTopologySuite();
            npgsql.MigrationsAssembly(typeof(HookDbContext).Assembly.GetName().Name);
        }));

    // Wolverine scrapes raised events from tracked AggregateRoot entities and
    // publishes them at EF tx commit — same path as direct PublishAsync, so
    // outgoing envelopes enrol in the durable outbox alongside the entity write.
    builder.Services.AddSingleton<IDomainEventScraper>(
        new DomainEventScraper<AggregateRoot, IDomainEvent>(agg => agg.DequeueEvents()));

    builder.Services.AddWhatsapp(builder.Configuration);
    builder.Services.AddConversationAi(builder.Configuration);
    builder.Services.AddServiceTaxonomy(builder.Configuration);
    builder.Services.AddGeocoding(builder.Configuration);
    builder.Services.AddProviderAvailability(builder.Configuration);
    builder.Services.AddServiceRequest();
    builder.Services.AddValidatedOptions<MatchingOptions>(builder.Configuration);
    builder.Services.AddMatching();
    builder.Services.AddRetentionSweeper(builder.Configuration);
    builder.Services.AddContactSharing();
    builder.Services.AddChat(builder.Configuration);
    builder.Services.AddChatLifecycle();
    builder.Services.AddChatPrivacyRouting();
    builder.Services.AddFeedback(builder.Configuration);
    builder.Services.AddRateLimiting(builder.Configuration);
    builder.Services.AddMetaTemplates();
    builder.Services.AddTips(builder.Configuration);
    builder.Services.AddObservability();

    builder.Services.AddSingleton<AiWarmupHostedService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AiWarmupHostedService>());
    if (builder.Environment.IsDevelopment() || builder.Environment.IsStaging())
    {
        builder.Services.AddHostedService<DevProviderSeederHostedService>();
    }

    builder.Services.AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

    // Caddy re-honours Content-Encoding upstream rather than re-compressing.
    builder.Services.AddResponseCompression(opts =>
    {
        opts.EnableForHttps = true;
        opts.Providers.Add<BrotliCompressionProvider>();
        opts.Providers.Add<GzipCompressionProvider>();
        opts.MimeTypes =
        [
            "text/html",
            "text/css",
            "text/plain",
            "text/xml",
            "application/javascript",
            "application/xml",
            "image/svg+xml",
        ];
    });

    builder.Services.AddGlobalRateLimiter(builder.Configuration);

    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    // Dev override so the Step1 feedback prompt surfaces in minutes instead of the
    // 30-minute production cadence during local hacking.
    if (builder.Environment.IsDevelopment())
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["Feedback:Step1InitialDelay"]))
            builder.Configuration["Feedback:Step1InitialDelay"] = "00:02:00";
    }

    builder.Host.UseWolverine(opts =>
    {
        // Wolverine's default 60s handler timeout preempts Ollama cold-start inference
        // (qwen2.5:3b on CPU can take 60-90s), aborting the socket mid-read. Align with
        // OllamaOptions.TimeoutSeconds plus a small buffer so HttpClient.Timeout governs.
        // Tests override via "Wolverine:DefaultExecutionTimeoutSeconds" since the test
        // fixture swaps in an in-memory IConversationAi.
        var overrideSeconds = builder.Configuration.GetValue<int?>(WolverineConfig.ExecutionTimeoutKey);
        if (overrideSeconds is int seconds && seconds > 0 && seconds <= 3600)
        {
            opts.DefaultExecutionTimeout = TimeSpan.FromSeconds(seconds);
        }
        else
        {
            var ollamaTimeout = builder.Configuration
                .GetSection(OllamaOptions.SectionName)
                .GetValue<int?>(nameof(OllamaOptions.TimeoutSeconds)) ?? 120;
            opts.DefaultExecutionTimeout = TimeSpan.FromSeconds(ollamaTimeout + 30);
        }

        // Dynamic IL emission to avoid Wolverine 6's ServiceLocationPolicy.NotAllowed
        // breaking opaque DI registrations (AddDbContext, AddHttpClient<T,Impl>).
        opts.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Dynamic;

        // Durable outbox in every environment so scheduled messages survive
        // restarts and handler commits stay atomic with outgoing envelopes.
        // Share the EF data source so Wolverine pulls connections from the
        // same pinned pool (no parallel pool with default min=0 / max=100).
        opts.PersistMessagesWithPostgresql(dataSource, schemaName: WolverineConfig.Schema);
        // Promote in-process local queues to durable so [NonTransactional] handlers
        // (Ollama AI stages) survive a crash between bus.PublishAsync and the inner
        // commit. Without this, locally-routed envelopes live only in memory.
        opts.Policies.UseDurableLocalQueues();
        opts.UseEntityFrameworkCoreTransactions();
        // Drains AggregateRoot.DequeueEvents() from tracked entities during the
        // AutoApplyTransactions middleware commit. Only fires inside a Wolverine
        // handler context — hubs/endpoints MUST dispatch a command and let the
        // handler own the aggregate mutation.
        opts.PublishDomainEventsFromEntityFrameworkCore();
        opts.Policies.AutoApplyTransactions();

        // Wolverine error policies — intentionally narrow:
        // (1) OCE during graceful shutdown is the documented drain path; discard so the
        //     next host start does not retry user-facing sends. Handler-local OCEs fall
        //     through to default (DLQ) so the bug is visible. IsStopping is armed by
        //     the IHostApplicationLifetime.ApplicationStopping subscription registered
        //     after app.Build() below; Environment.HasShutdownStarted is a belt-and-
        //     suspenders fallback for late finalizer paths.
        // (2) Transient PG split into fast (deadlock/serialization, <1s cooldown) + slow
        //     (connection storm, multi-second cooldown). Both walk InnerException so EF's
        //     DbUpdateException(PostgresException) wrap also retries. Non-transient PG
        //     bubbles to default (no retry, DLQ).
        // HttpRequestException is NOT policied here: HTTP callers (WhatsApp, Geocoding)
        // install AddStandardResilienceHandler (Polly retries + circuit breaker) at the
        // HttpClient layer — stacking another retry would double-up + risk duplicate POSTs.
        // OCE-Discard semantics covered by WolverineErrorPolicyTests.HandlerLocalOperation
        // CanceledFallsThroughToDeadLetterNotDiscarded + ShutdownOceDiscardedNoDeadLetter.
        opts.Policies.OnException<OperationCanceledException>(_ => WolverineShutdownGate.IsStopping)
            .Discard();
        // Fast tier: deadlock victims + serialization-failures clear in <100ms.
        opts.Policies.OnException<PostgresException>(ex => TransientPgStates.IsTransientFast(ex.SqlState))
            .RetryWithCooldown(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2));
        opts.Policies.OnException<DbUpdateException>(ex => TransientPgStates.IsTransientFast(ex))
            .RetryWithCooldown(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2));
        // Slow tier: connection storms + too-many-connections need real wait.
        opts.Policies.OnException<PostgresException>(ex => TransientPgStates.IsTransientSlow(ex.SqlState))
            .RetryWithCooldown(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
        opts.Policies.OnException<DbUpdateException>(ex => TransientPgStates.IsTransientSlow(ex))
            .RetryWithCooldown(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
    });

    var app = builder.Build();

    // Arm WolverineShutdownGate at the start of graceful drain so the OCE-Discard
    // policy above sees IsStopping == true before Wolverine.StopAsync cancels
    // in-flight handlers. Environment.HasShutdownStarted alone is too late — it
    // only flips during AppDomain teardown, after the drain has finished.
    app.Lifetime.ApplicationStopping.Register(WolverineShutdownGate.Trip);

    {
        // Migrations are applied out-of-band (CI deploy step, or `dotnet ef database
        // update` locally). At boot we only verify the schema head — running the full
        // MigrateAsync scan/apply on every cold start blocked Kestrel bind for hundreds
        // of ms with 50+ migrations. Fail fast so a missed deploy step is loud.
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                $"Pending migrations not applied: {string.Join(", ", pending)}. " +
                "Run `dotnet ef database update` before starting the host.");
        }

        var rootSeeder = scope.ServiceProvider.GetRequiredService<RootSectorSeeder>();
        await rootSeeder.EnsureRootSectorsAsync();
    }

    // Dev runs Kestrel directly with no proxy; trust forwarded headers only outside Development.
    if (!builder.Environment.IsDevelopment())
    {
        app.UseForwardedHeaders(ForwardedHeadersConfigurator.Build(builder.Configuration));
    }

    // Full baseline of response security headers (CSP, HSTS in prod, frame/COOP/etc.)
    // also covers the Referrer-Policy strip needed for the token-in-URL chat pattern.
    app.UseSecurityHeaders(app.Environment);
    app.UseResponseCompression();
    app.UseExceptionHandler();
    app.UseObservability();
    app.UseRateLimiter();
    app.UseSerilogRequestLogging(opts =>
    {
        opts.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            // Scrub `?token=` and `?sessionId=` from any request-log query-string property.
            var qs = httpContext.Request.QueryString.ToString();
            if (!string.IsNullOrEmpty(qs))
            {
                diagnosticContext.Set("QueryString", RequestLogScrub.Scrub(qs));
            }
        };
    });

    app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
    app.MapGet("/readyz", async (AiReadinessProbe probe, CancellationToken ct) =>
    {
        var result = await probe.ProbeAsync(ct);
        var payload = new { ok = result.Healthy, checkedAt = result.CheckedAt, error = result.Error };
        return result.Healthy
            ? Results.Ok(payload)
            : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
    });
    app.MapPrometheusScrapingEndpoint();
    app.MapWhatsappWebhook();

    // Belt-and-suspenders: an accidental DevWhatsapp__Enabled=true in prod would
    // expose inbound spoofing + AI/WhatsApp sends to any E.164. IsProduction()
    // makes the prod exposure structurally impossible.
    if (!builder.Environment.IsProduction())
    {
        if (builder.Configuration.GetValue<bool>($"{DevWhatsappOptions.SectionName}:Enabled"))
        {
            app.MapDevWhatsapp();
        }
        if (builder.Configuration.GetValue<bool>($"{DevProviderSeedOptions.SectionName}:Enabled"))
        {
            app.MapDevProviders();
        }
    }
    app.MapChat();
    app.MapReverseProxy();
    app.UseDefaultFiles();
    // Cache content-hashed /assets forever; force revalidation on everything at the root.
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            var path = ctx.Context.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
            }
            else
            {
                ctx.Context.Response.Headers["Cache-Control"] = "no-cache";
            }
        }
    });
    app.MapHub<ChatHub>(ChatHubConstants.HubPath);
    app.MapFallbackToFile("index.html");

    await app.StartAsync();

    {
        // Kestrel is already bound at this point; this wait only defers
        // WaitForShutdownAsync so /readyz returning 503 lasts at most ~15s in
        // happy cases (warmup usually completes faster). WarmupCompletion resolves
        // ONLY on successful warm-up — budget elapsed and transport failure leave
        // it unresolved so the Delay wins and the warning below fires. The strict
        // gate for traffic is /readyz, not this delay.
        var warmup = app.Services.GetRequiredService<AiWarmupHostedService>();
        var completed = await Task.WhenAny(warmup.WarmupCompletion, Task.Delay(TimeSpan.FromSeconds(15)));
        if (completed != warmup.WarmupCompletion)
        {
            app.Logger.LogWarning("AI warmup did not complete within 15s — starting anyway, /readyz will gate.");
        }
    }

    await app.WaitForShutdownAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Hook host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

namespace Hook
{
    public partial class Program;

    internal static class RequestLogScrub
    {
        private static readonly HashSet<string> SensitiveKeys =
            new(StringComparer.OrdinalIgnoreCase) { "token", "sessionId" };

        public static string Scrub(string queryString)
        {
            if (string.IsNullOrEmpty(queryString)) return queryString;
            var hasPrefix = queryString.StartsWith('?');
            var body = hasPrefix ? queryString[1..] : queryString;
            var rewritten = string.Join('&', body.Split('&').Select(part =>
            {
                var eq = part.IndexOf('=');
                if (eq < 0) return part;
                var name = part[..eq];
                return SensitiveKeys.Contains(name) ? $"{name}=***" : part;
            }));
            return hasPrefix ? "?" + rewritten : rewritten;
        }
    }
}
