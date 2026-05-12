using System.Net;
using System.Net.Http.Json;
using Hook.Shared.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Hook.IntegrationTests.Forwarding;

public class ForwardedHeadersWiringTests
{
    private sealed record EchoPayload(string? Ip, string Scheme);

    private static async Task<IHost> StartAsync(Dictionary<string, string?> cfg, IPAddress? simulatedClient = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(cfg));
                webBuilder.Configure((context, app) =>
                {
                    // TestServer leaves Connection.RemoteIpAddress null. Stamp it so the
                    // trust gate has a concrete address to evaluate — mirrors what
                    // Kestrel would have set in production.
                    if (simulatedClient is not null)
                    {
                        app.Use(async (ctx, next) =>
                        {
                            ctx.Connection.RemoteIpAddress = simulatedClient;
                            await next();
                        });
                    }
                    app.UseForwardedHeaders(ForwardedHeadersConfigurator.Build(context.Configuration));
                    app.Run(async ctx =>
                    {
                        await ctx.Response.WriteAsJsonAsync(new EchoPayload(
                            ctx.Connection.RemoteIpAddress?.ToString(),
                            ctx.Request.Scheme));
                    });
                });
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    private static readonly IPAddress BridgeClient = IPAddress.Parse("172.17.0.5");
    private static readonly IPAddress PublicClient = IPAddress.Parse("203.0.113.99");

    [Fact]
    public async Task TrustedClient_XForwardedFor_RewritesRemoteIp()
    {
        using var host = await StartAsync(
            new() { ["ForwardedHeaders:KnownNetworks:0"] = "172.16.0.0/12" },
            simulatedClient: BridgeClient);

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");

        var payload = await (await client.GetAsync("/echo")).Content.ReadFromJsonAsync<EchoPayload>();
        payload.ShouldNotBeNull();
        payload!.Ip.ShouldBe("203.0.113.7");
    }

    [Fact]
    public async Task UntrustedClient_XForwardedFor_Ignored()
    {
        // Simulated client lives outside the trust CIDR → header dropped.
        using var host = await StartAsync(
            new() { ["ForwardedHeaders:KnownNetworks:0"] = "172.16.0.0/12" },
            simulatedClient: PublicClient);

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");

        var payload = await (await client.GetAsync("/echo")).Content.ReadFromJsonAsync<EchoPayload>();
        payload.ShouldNotBeNull();
        payload!.Ip.ShouldBe(PublicClient.ToString());
    }

    [Fact]
    public async Task MalformedCidr_IsSilentlyIgnored_StackStillBoots()
    {
        using var host = await StartAsync(
            new()
            {
                ["ForwardedHeaders:KnownNetworks:0"] = "not-a-cidr",
                ["ForwardedHeaders:KnownNetworks:1"] = "172.16.0.0/12"
            },
            simulatedClient: BridgeClient);

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");

        var payload = await (await client.GetAsync("/echo")).Content.ReadFromJsonAsync<EchoPayload>();
        payload.ShouldNotBeNull();
        payload!.Ip.ShouldBe("203.0.113.7");
    }

    [Fact]
    public async Task ForwardLimitOne_HonorsLastHopOnly()
    {
        using var host = await StartAsync(
            new() { ["ForwardedHeaders:KnownNetworks:0"] = "172.16.0.0/12" },
            simulatedClient: BridgeClient);

        var client = host.GetTestClient();
        // With ForwardLimit=1 only the *last* entry is consumed.
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "1.1.1.1, 2.2.2.2, 3.3.3.3");

        var payload = await (await client.GetAsync("/echo")).Content.ReadFromJsonAsync<EchoPayload>();
        payload.ShouldNotBeNull();
        payload!.Ip.ShouldBe("3.3.3.3");
    }

    [Fact]
    public async Task XForwardedProto_FlipsScheme()
    {
        using var host = await StartAsync(
            new() { ["ForwardedHeaders:KnownNetworks:0"] = "172.16.0.0/12" },
            simulatedClient: BridgeClient);

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var payload = await (await client.GetAsync("/echo")).Content.ReadFromJsonAsync<EchoPayload>();
        payload.ShouldNotBeNull();
        payload!.Scheme.ShouldBe("https");
    }
}
