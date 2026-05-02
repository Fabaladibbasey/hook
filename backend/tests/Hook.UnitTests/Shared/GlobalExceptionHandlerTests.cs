using System.Text.Json;
using Hook.Shared.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Hook.UnitTests.Shared;

public class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task NonProduction_ReturnsProblemDetailsWithStackTrace()
    {
        var (handler, ctx) = Build("Development");
        var exception = new InvalidOperationException("boom");

        var handled = await handler.TryHandleAsync(ctx, exception, CancellationToken.None);

        handled.ShouldBeTrue();
        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        var body = await ReadBodyAsync(ctx);
        body.GetProperty("title").GetString().ShouldBe("boom");
        body.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("InvalidOperationException");
        body.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
        body.GetProperty("method").GetString().ShouldBe("GET");
        body.GetProperty("path").GetString().ShouldBe("/test");
    }

    [Fact]
    public async Task Production_RedactsTitleAndOmitsDetail()
    {
        var (handler, ctx) = Build("Production");
        var exception = new InvalidOperationException("secret-leak");

        var handled = await handler.TryHandleAsync(ctx, exception, CancellationToken.None);

        handled.ShouldBeTrue();
        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        var body = await ReadBodyAsync(ctx);
        body.GetProperty("title").GetString().ShouldBe("An unexpected error occurred.");
        body.TryGetProperty("detail", out _).ShouldBeFalse();
        body.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
    }

    private static (GlobalExceptionHandler handler, HttpContext ctx) Build(string environmentName)
    {
        var env = new HostingEnvironment { EnvironmentName = environmentName };
        var handler = new GlobalExceptionHandler(env, NullLogger<GlobalExceptionHandler>.Instance);
        var ctx = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
            TraceIdentifier = "trace-abc",
        };
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/test";
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("localhost");
        return (handler, ctx);
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        return doc.RootElement.Clone();
    }

    private sealed class HostingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Hook.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
