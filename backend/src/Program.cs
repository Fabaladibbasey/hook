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
    builder.Services.AddContactSharing();
    builder.Services.AddChat(builder.Configuration);
    builder.Services.AddChatLifecycle();
    builder.Services.AddChatPrivacyRouting();
    builder.Services.AddFeedback();
    builder.Services.AddRateLimiting(builder.Configuration);
    builder.Services.AddMetaTemplates();
    builder.Services.AddObservability();

    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    builder.Host.UseWolverine();

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
    app.UseSerilogRequestLogging();

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
}
