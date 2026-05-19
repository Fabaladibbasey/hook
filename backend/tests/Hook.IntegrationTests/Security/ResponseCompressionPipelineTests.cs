using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Hook.IntegrationTests.Security;

public class ResponseCompressionPipelineTests
{
    private static async Task<HttpClient> ClientAsync()
    {
        // Mirrors the production registration in Program.cs — keep in sync.
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s => s.AddResponseCompression(o =>
                {
                    o.EnableForHttps = true;
                    o.Providers.Add<BrotliCompressionProvider>();
                    o.Providers.Add<GzipCompressionProvider>();
                    o.MimeTypes =
                    [
                        "text/html",
                        "text/css",
                        "text/plain",
                        "text/xml",
                        "application/javascript",
                        "application/xml",
                        "image/svg+xml",
                    ];
                }));
                web.Configure(app =>
                {
                    app.UseResponseCompression();
                    app.Run(async ctx =>
                    {
                        ctx.Response.ContentType = ctx.Request.Query["ct"].ToString();
                        await ctx.Response.WriteAsync(new string('a', 4096));
                    });
                });
            })
            .Build();
        await host.StartAsync();
        return host.GetTestClient();
    }

    [Theory]
    [InlineData("text/html", "br")]
    [InlineData("application/javascript", "br")]
    [InlineData("image/svg+xml", "br")]
    public async Task AllowListedMime_WithBrotli_Compressed(string contentType, string expectedEncoding)
    {
        var client = await ClientAsync();
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        var response = await client.GetAsync($"/?ct={Uri.EscapeDataString(contentType)}");
        response.Content.Headers.ContentEncoding.ShouldContain(expectedEncoding);
    }

    [Fact]
    public async Task NoBrotli_FallsBackToGzip()
    {
        var client = await ClientAsync();
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        var response = await client.GetAsync("/?ct=text/html");
        response.Content.Headers.ContentEncoding.ShouldContain("gzip");
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("image/png")]
    public async Task NonAllowedMime_NotCompressed(string contentType)
    {
        var client = await ClientAsync();
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        var response = await client.GetAsync($"/?ct={Uri.EscapeDataString(contentType)}");
        response.Content.Headers.ContentEncoding.ShouldBeEmpty();
    }
}
