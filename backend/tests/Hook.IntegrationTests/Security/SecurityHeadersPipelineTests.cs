using System.Net;
using Hook.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Hook.IntegrationTests.Security;

public class SecurityHeadersPipelineTests
{
    private static async Task<IHost> StartAsync(string environmentName, Action<IApplicationBuilder>? extra = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.UseEnvironment(environmentName);
                webBuilder.ConfigureServices(s =>
                {
                    s.AddProblemDetails();
                    s.AddExceptionHandler<Hook.Shared.Core.GlobalExceptionHandler>();
                });
                webBuilder.Configure((context, app) =>
                {
                    app.UseExceptionHandler();
                    app.UseSecurityHeaders(context.HostingEnvironment);
                    extra?.Invoke(app);
                    app.Run(async ctx => await ctx.Response.WriteAsync("ok"));
                });
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task ProductionEnv_HappyPath_EmitsAllBaselineHeadersIncludingHsts()
    {
        using var host = await StartAsync("Production");
        var response = await host.GetTestClient().GetAsync("/");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Content-Security-Policy").ShouldContain(v => v.Contains("default-src 'self'"));
        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        response.Headers.GetValues("X-Frame-Options").ShouldContain("DENY");
        response.Headers.GetValues("Referrer-Policy").ShouldContain("no-referrer");
        response.Headers.GetValues("Permissions-Policy").ShouldContain(v => v.Contains("camera=()"));
        response.Headers.GetValues("Cross-Origin-Opener-Policy").ShouldContain("same-origin");
        response.Headers.GetValues("Strict-Transport-Security").ShouldContain(v => v.Contains("max-age=63072000"));
    }

    [Fact]
    public async Task DevelopmentEnv_OmitsHsts()
    {
        using var host = await StartAsync("Development");
        var response = await host.GetTestClient().GetAsync("/");
        response.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();
        response.Headers.Contains("Content-Security-Policy").ShouldBeTrue();
    }

    [Fact]
    public async Task ThrownEndpoint_ProblemDetailsResponse_StillCarriesSecurityHeaders()
    {
        using var host = await StartAsync("Production", app =>
            app.Run(_ => throw new InvalidOperationException("boom")));
        var response = await host.GetTestClient().GetAsync("/boom");
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Headers.GetValues("Content-Security-Policy").ShouldContain(v => v.Contains("default-src 'self'"));
        response.Headers.GetValues("X-Frame-Options").ShouldContain("DENY");
        response.Headers.GetValues("Strict-Transport-Security").ShouldContain(v => v.Contains("max-age=63072000"));
    }
}
