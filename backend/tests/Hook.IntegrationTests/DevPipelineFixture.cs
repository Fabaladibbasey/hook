using Hook.Features.Ai;
using Hook.Features.ProviderAvailability.Dev;
using Hook.Shared.Persistence.Data;
using Hook.TestHelpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace Hook.IntegrationTests;

public sealed class DevPipelineFixture : IAsyncLifetime
{
    public const double SeedRefLat = 37.7749;
    public const double SeedRefLng = -122.4194;

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
        "GoogleGeocoding__ApiKey",
        "Dev__Whatsapp__Enabled",
        "Dev__Geocoding__Enabled",
        "Dev__Providers__Enabled",
        "Dev__Providers__AutoSeed",
        "Dev__Providers__ReferenceLat",
        "Dev__Providers__ReferenceLng",
        "Dev__Providers__TtlHours"
    ];

    public async Task InitializeAsync()
    {
        await Db.StartAsync();

        Environment.SetEnvironmentVariable("ConnectionStrings__HookDb", Db.GetConnectionString());
        Environment.SetEnvironmentVariable("Whatsapp__VerifyToken", "v");
        Environment.SetEnvironmentVariable("Whatsapp__AppSecret", "s");
        Environment.SetEnvironmentVariable("Whatsapp__PhoneNumberId", "PN-1");
        Environment.SetEnvironmentVariable("Whatsapp__AccessToken", "token");
        Environment.SetEnvironmentVariable("GoogleGeocoding__ApiKey", "k");

        Environment.SetEnvironmentVariable("Dev__Whatsapp__Enabled", "true");
        Environment.SetEnvironmentVariable("Dev__Geocoding__Enabled", "true");

        Environment.SetEnvironmentVariable("Dev__Providers__Enabled", "true");
        Environment.SetEnvironmentVariable("Dev__Providers__AutoSeed", "true");
        Environment.SetEnvironmentVariable(
            "Dev__Providers__ReferenceLat", SeedRefLat.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(
            "Dev__Providers__ReferenceLng", SeedRefLng.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("Dev__Providers__TtlHours", "24");

        Factory = new WebApplicationFactory<global::Hook.Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.ConfigureTestServices(s =>
                {
                    s.RemoveAll<IConversationAi>();
                    s.AddSingleton<IConversationAi, FakeConversationAi>();
                });
            });

        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        await ctx.Database.EnsureCreatedAsync();

        var seeder = scope.ServiceProvider.GetRequiredService<DevProviderSeeder>();
        await seeder.SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
        foreach (var key in EnvKeys)
            Environment.SetEnvironmentVariable(key, null);
    }
}
