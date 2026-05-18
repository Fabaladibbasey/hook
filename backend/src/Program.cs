using Hook;
using Hook.Features.Ai;
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
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Dev;
using Hook.Features.Whatsapp.ReceiveWebhook;
using Hook.Shared.Core;
using Hook.Shared.Domain;
using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;
using Hook.Shared.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Wolverine;
using Wolverine.EntityFrameworkCore;
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

    builder.Host.UseSerilog((context, services, config) => config
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    var connectionString = builder.Configuration.GetConnectionString("HookDb")
        ?? throw new InvalidOperationException("Connection string 'HookDb' is not configured.");

    // AddDbContextWithWolverineIntegration installs a model customizer that
    // requires Wolverine.RDBMS.DatabaseSettings in DI — paired with
    // PersistMessagesWithPostgresql in the UseWolverine block so every
    // environment (including tests) gets the EF transactional outbox.
    // Per-test isolation relies on each fixture using its own Postgres database.
    builder.Services.AddDbContextWithWolverineIntegration<HookDbContext>(options =>
        options.UseNpgsql(connectionString, npgsql =>
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
    builder.Services.AddObservability();

    builder.Services.AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

    builder.Services.AddGlobalRateLimiter(builder.Configuration);

    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    // Dev default so the Step1 feedback prompt surfaces in minutes instead of the
    // 30-minute production cadence during local hacking. Step2 is now published
    // immediately on Step1=Yes (Pillar A — no separate delay knob), so only Step1
    // is overridden here. Devs can still override via env or appsettings.Development.json.
    if (builder.Environment.IsDevelopment())
    {
        // IsNullOrWhiteSpace catches the (admittedly unlikely) "   " case as well as
        // truly-unset; keeping the dev override to a 2-min cadence so the prompt
        // fires inside a single hacking session.
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
        opts.PersistMessagesWithPostgresql(connectionString, schemaName: WolverineConfig.Schema);
        opts.UseEntityFrameworkCoreTransactions();
        // Drains AggregateRoot.DequeueEvents() from tracked entities during the
        // AutoApplyTransactions middleware commit. Note: only fires inside a Wolverine
        // handler — non-handler SaveChanges (e.g. ChatHub.EndChat) must drain manually
        // via IMessageBus.PublishAsync after SaveChanges.
        opts.PublishDomainEventsFromEntityFrameworkCore();
        opts.Policies.AutoApplyTransactions();
    });

    var app = builder.Build();

    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        await db.Database.MigrateAsync();

        if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
        {
            var seedOpts = scope.ServiceProvider
                .GetRequiredService<IOptions<DevProviderSeedOptions>>().Value;
            if (seedOpts.Enabled && seedOpts.AutoSeed)
            {
                var seeder = scope.ServiceProvider.GetRequiredService<DevProviderSeeder>();
                await seeder.SeedAsync();
            }
        }
    }

    // Dev runs Kestrel directly with no proxy; trust forwarded headers only outside Development.
    if (!builder.Environment.IsDevelopment())
    {
        app.UseForwardedHeaders(ForwardedHeadersConfigurator.Build(builder.Configuration));
    }

    // Token-in-URL pattern leaks via Referer; strip referrer on all responses.
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        await next();
    });
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

    if (builder.Configuration.GetValue<bool>($"{DevWhatsappOptions.SectionName}:Enabled"))
    {
        app.MapDevWhatsapp();
    }
    if (builder.Configuration.GetValue<bool>($"{DevProviderSeedOptions.SectionName}:Enabled"))
    {
        app.MapDevProviders();
    }
    app.MapChat();
    app.MapReverseProxy();
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapHub<ChatHub>(ChatHubConstants.HubPath);
    app.MapFallbackToFile("index.html");

    await app.RunAsync();
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
        private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase) { "token", "sessionId" };

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
