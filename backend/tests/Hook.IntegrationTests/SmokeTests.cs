using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hook.Shared.Persistence.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Testcontainers.PostgreSql;

namespace Hook.IntegrationTests;

public sealed class HookAppFixture : IAsyncLifetime
{
    public const string VerifyToken = "smoke-verify";
    public const string AppSecret = "smoke-secret";

    public PostgreSqlContainer Db { get; } = new PostgreSqlBuilder("postgis/postgis:16-3.4")
        .WithDatabase("hook")
        .WithUsername("hook")
        .WithPassword("hook")
        .Build();

    public WebApplicationFactory<global::Hook.Program> Factory { get; private set; } = default!;

    private static readonly string[] EnvKeys =
    [
        "ConnectionStrings__HookDb",
        "Whatsapp__VerifyToken",
        "Whatsapp__AppSecret",
        "Whatsapp__PhoneNumberId",
        "Whatsapp__AccessToken",
        "GoogleGeocoding__ApiKey"
    ];

    public async Task InitializeAsync()
    {
        await Db.StartAsync();

        Environment.SetEnvironmentVariable("ConnectionStrings__HookDb", Db.GetConnectionString());
        Environment.SetEnvironmentVariable("Whatsapp__VerifyToken", VerifyToken);
        Environment.SetEnvironmentVariable("Whatsapp__AppSecret", AppSecret);
        Environment.SetEnvironmentVariable("Whatsapp__PhoneNumberId", "PN-1");
        Environment.SetEnvironmentVariable("Whatsapp__AccessToken", "token");
        Environment.SetEnvironmentVariable("GoogleGeocoding__ApiKey", "k");

        Factory = new WebApplicationFactory<global::Hook.Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Test"));

        // Force host build & create schema from current model.
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
        foreach (var key in EnvKeys)
            Environment.SetEnvironmentVariable(key, null);
    }
}

public class SmokeTests : IClassFixture<HookAppFixture>
{
    private readonly HookAppFixture _fx;

    public SmokeTests(HookAppFixture fx) => _fx = fx;

    [Fact]
    public async Task Healthz_ReturnsOk()
    {
        using var client = _fx.Factory.CreateClient();
        var resp = await client.GetAsync("/healthz");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Metrics_ReturnsPrometheusContent()
    {
        using var client = _fx.Factory.CreateClient();
        var resp = await client.GetAsync("/metrics");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldContain("# TYPE");
    }

    [Fact]
    public async Task WebhookVerify_AcceptsCorrectToken()
    {
        using var client = _fx.Factory.CreateClient();
        var resp = await client.GetAsync(
            $"/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token={HookAppFixture.VerifyToken}&hub.challenge=42");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).ShouldBe("42");
    }

    [Fact]
    public async Task WebhookVerify_RejectsBadToken()
    {
        using var client = _fx.Factory.CreateClient();
        var resp = await client.GetAsync("/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=42");
        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WebhookPost_RejectsBadSignature()
    {
        using var client = _fx.Factory.CreateClient();
        var content = new StringContent("{\"object\":\"whatsapp_business_account\",\"entry\":[]}",
            Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/whatsapp") { Content = content };
        req.Headers.TryAddWithoutValidation("X-Hub-Signature-256", "sha256=deadbeef");

        var resp = await client.SendAsync(req);
        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WebhookPost_AcceptsValidSignature()
    {
        using var client = _fx.Factory.CreateClient();
        var bodyJson = "{\"object\":\"whatsapp_business_account\",\"entry\":[]}";
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        var sig = "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(HookAppFixture.AppSecret), bodyBytes));

        using var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/whatsapp")
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("X-Hub-Signature-256", sig);

        var resp = await client.SendAsync(req);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
