using System.Threading.RateLimiting;
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
using Hook.Shared.Persistence.Data;
using Hook.Shared.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Wolverine;

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

    builder.Services.AddDbContext<HookDbContext>(options =>
        options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNetTopologySuite();
            npgsql.MigrationsAssembly(typeof(HookDbContext).Assembly.GetName().Name);
        }));

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

    builder.Services.AddRateLimiter(opts =>
    {
        opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        opts.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        {
            var token = ctx.Request.Query["token"].ToString();
            var key = !string.IsNullOrEmpty(token) ? $"t:{token}" : $"ip:{ctx.Connection.RemoteIpAddress}";
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(5),
                PermitLimit = 3,
                QueueLimit = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
        });
    });

    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    builder.Host.UseWolverine(opts =>
    {
        // Wolverine's default 60s handler timeout preempts Ollama cold-start inference
        // (qwen2.5:3b on CPU can take 60-90s), aborting the socket mid-read. Align with
        // OllamaOptions.TimeoutSeconds plus a small buffer so HttpClient.Timeout governs.
        var ollamaTimeout = builder.Configuration
            .GetSection(OllamaOptions.SectionName)
            .GetValue<int?>(nameof(OllamaOptions.TimeoutSeconds)) ?? 120;
        opts.DefaultExecutionTimeout = TimeSpan.FromSeconds(ollamaTimeout + 30);
    });

    var app = builder.Build();

    if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        await db.Database.MigrateAsync();

        var seedOpts = scope.ServiceProvider
            .GetRequiredService<IOptions<DevProviderSeedOptions>>().Value;
        if (seedOpts.Enabled && seedOpts.AutoSeed)
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DevProviderSeeder>();
            await seeder.SeedAsync();
        }
    }

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
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapHub<ChatHub>("/hubs/chat");
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
            var qs = queryString.StartsWith('?') ? queryString[1..] : queryString;
            var rewritten = string.Join('&', qs.Split('&').Select(part =>
            {
                var eq = part.IndexOf('=');
                if (eq < 0) return part;
                var name = part[..eq];
                return SensitiveKeys.Contains(name) ? $"{name}=***" : part;
            }));
            return queryString.StartsWith('?') ? "?" + rewritten : rewritten;
        }
    }
}
