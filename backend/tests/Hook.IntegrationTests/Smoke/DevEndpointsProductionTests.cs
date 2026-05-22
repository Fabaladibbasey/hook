using System.Net;
using System.Net.Http.Json;
using Hook.Shared.Persistence.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace Hook.IntegrationTests.Smoke;

public sealed class ProdEnvAppFixture : IAsyncLifetime
{
    public PostgreSqlContainer Db { get; } = new PostgreSqlBuilder("postgis/postgis:16-3.4")
        .WithDatabase("hook")
        .WithUsername("hook")
        .WithPassword("hook")
        .Build();

    public WebApplicationFactory<global::Hook.Program> Factory { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await Db.StartAsync();

        var connectionString = Db.GetConnectionString();
        var opts = new DbContextOptionsBuilder<HookDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseNetTopologySuite())
            .Options;
        await using (var migrateCtx = new HookDbContext(opts))
        {
            await migrateCtx.Database.MigrateAsync();
        }

        Factory = new WebApplicationFactory<global::Hook.Program>()
            .WithWebHostBuilder(b =>
            {
                // Production env even though DevWhatsapp__Enabled=true — the env-guard
                // in Program.cs must structurally suppress the dev endpoint mapping.
                b.UseEnvironment("Production");
                b.UseSetting("ConnectionStrings:HookDb", connectionString);
                b.UseSetting("Whatsapp:VerifyToken", "v");
                b.UseSetting("Whatsapp:AppSecret", "s");
                b.UseSetting("Whatsapp:PhoneNumberId", "PN");
                b.UseSetting("Whatsapp:AccessToken", "t");
                b.UseSetting("GoogleGeocoding:ApiKey", "k");
                b.UseSetting("DevWhatsapp:Enabled", "true");
                b.UseSetting("DevProviderSeed:Enabled", "true");
                b.UseSetting("Retention:Enabled", "false");
            });

        await DevPipelineFixture.HostInitLock.WaitAsync();
        try
        {
            _ = Factory.Services;
        }
        finally
        {
            DevPipelineFixture.HostInitLock.Release();
        }
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
    }
}

public class DevEndpointsProductionTests : IClassFixture<ProdEnvAppFixture>
{
    private readonly ProdEnvAppFixture _fx;

    public DevEndpointsProductionTests(ProdEnvAppFixture fx) => _fx = fx;

    // MapFallbackToFile serves index.html for any unrouted GET, so a 200 isn't
    // proof the dev endpoint ran. We assert the response is the SPA fallback
    // (HTML) — when the dev endpoint runs it returns application/json.
    private static void ShouldBeSpaFallback(HttpResponseMessage resp)
    {
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        resp.Content.Headers.ContentType?.MediaType.ShouldNotBe("application/json");
    }

    [Fact]
    public async Task DevWhatsappOutbox_InProduction_FallsThroughToSpa()
    {
        using var client = _fx.Factory.CreateClient();
        var resp = await client.GetAsync("/dev/whatsapp/outbox");
        ShouldBeSpaFallback(resp);
    }

    [Fact]
    public async Task DevWhatsappStatus_InProduction_FallsThroughToSpa()
    {
        using var client = _fx.Factory.CreateClient();
        var resp = await client.GetAsync("/dev/whatsapp/status");
        ShouldBeSpaFallback(resp);
    }

    [Fact]
    public async Task DevWhatsappInboundPost_InProduction_Returns404()
    {
        // POST has no SPA fallback to mask the gating; if the dev endpoint were
        // mapped it would accept the body and return 200, otherwise routing
        // returns 404. This is the strongest signal that the env-guard works.
        using var client = _fx.Factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/dev/whatsapp/inbound", new
        {
            From = "+22070123456",
            Text = "should be blocked"
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DevProviders_InProduction_FallsThroughToSpa()
    {
        using var client = _fx.Factory.CreateClient();
        var resp = await client.GetAsync("/dev/providers");
        ShouldBeSpaFallback(resp);
    }
}
