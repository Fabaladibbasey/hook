using System.Text.Json;
using Hook.Shared.Core;
using Hook.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;

namespace Hook.UnitTests.Shared;

public class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task NonProduction_ReturnsProblemDetailsWithTraceAndType()
    {
        var (handler, ctx, _) = Build("Development");
        var exception = new InvalidOperationException("boom");

        var handled = await handler.TryHandleAsync(ctx, exception, CancellationToken.None);

        handled.ShouldBeTrue();
        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        var body = await ReadBodyAsync(ctx);
        body.GetProperty("title").GetString().ShouldBe("InvalidOperationException");
        // Detail no longer echoes exception.Message — PII risk.
        body.GetProperty("detail").GetString().ShouldNotBeNull().ShouldNotContain("boom");
        body.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
        body.GetProperty("method").GetString().ShouldBe("GET");
        body.GetProperty("instance").GetString().ShouldEndWith("/test");
        body.GetProperty("path").GetString().ShouldBe("/test");
    }

    [Fact]
    public async Task Detail_DoesNotEcho_PostgresExceptionMessage_InNonProduction()
    {
        // PostgresException.Message embeds the duplicate-key value — frequently
        // a phone number. Detail must not leak it even in non-prod responses.
        var (handler, ctx, _) = Build("Development");
        var pg = new PostgresException(
            messageText: "duplicate key value violates unique constraint — phone=+220300001",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.UniqueViolation);

        await handler.TryHandleAsync(ctx, pg, CancellationToken.None);

        var body = await ReadBodyAsync(ctx);
        var detail = body.GetProperty("detail").GetString().ShouldNotBeNull();
        detail.ShouldNotContain("+220300001");
        detail.ShouldNotContain("duplicate key");
    }

    [Fact]
    public async Task Verbose_DoesNotEmit_QueryString_ExtensionField()
    {
        // queryString in the response body was the egress for sensitive query
        // params. The operator can correlate via traceId in server logs instead.
        var (handler, ctx, _) = Build("Development");
        ctx.Request.QueryString = new QueryString("?token=abc123");

        await handler.TryHandleAsync(ctx, new InvalidOperationException("boom"), CancellationToken.None);

        var body = await ReadBodyAsync(ctx);
        body.TryGetProperty("queryString", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Maps_AggregateException_WrappingUniqueViolation_To_409()
    {
        var (handler, ctx, _) = Build("Production");
        var pg = new PostgresException(
            messageText: "dup",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.UniqueViolation);
        var agg = new AggregateException(new InvalidOperationException("noise"), pg);

        await handler.TryHandleAsync(ctx, agg, CancellationToken.None);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Maps_3LevelChain_AggregateOfDbUpdateOfUnique_To_409()
    {
        var (handler, ctx, _) = Build("Production");
        var pg = new PostgresException(
            messageText: "dup",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.UniqueViolation);
        var ef = new DbUpdateException("ef wrap", pg);
        var agg = new AggregateException(ef);

        await handler.TryHandleAsync(ctx, agg, CancellationToken.None);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Production_RedactsTitleAndOmitsDetail()
    {
        var (handler, ctx, _) = Build("Production");
        var exception = new InvalidOperationException("secret-leak");

        var handled = await handler.TryHandleAsync(ctx, exception, CancellationToken.None);

        handled.ShouldBeTrue();
        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        var body = await ReadBodyAsync(ctx);
        body.GetProperty("title").GetString().ShouldBe("An unexpected error occurred.");
        body.TryGetProperty("detail", out _).ShouldBeFalse();
        body.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    [InlineData(StatusCodes.Status414UriTooLong)]
    [InlineData(StatusCodes.Status431RequestHeaderFieldsTooLarge)]
    public async Task Maps_BadHttpRequestException_PreservesKestrelStatus(int kestrelStatus)
    {
        var (handler, ctx, _) = Build("Production");

        await handler.TryHandleAsync(ctx, new BadHttpRequestException("bad", kestrelStatus), CancellationToken.None);

        ctx.Response.StatusCode.ShouldBe(kestrelStatus);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task BadHttpRequestException_NonClientStatus_FallsBackTo_400(int kestrelStatus)
    {
        var (handler, ctx, _) = Build("Production");

        await handler.TryHandleAsync(ctx, new BadHttpRequestException("bad", kestrelStatus), CancellationToken.None);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Maps_JsonException_To_400()
    {
        var (handler, ctx, _) = Build("Production");

        await handler.TryHandleAsync(ctx, new JsonException("malformed"), CancellationToken.None);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Maps_PostgresUniqueViolation_To_409()
    {
        var (handler, ctx, _) = Build("Production");
        var pg = new PostgresException(
            messageText: "dup",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.UniqueViolation);

        await handler.TryHandleAsync(ctx, pg, CancellationToken.None);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Maps_DbUpdateException_WrappingUniqueViolation_To_409()
    {
        var (handler, ctx, _) = Build("Production");
        var pg = new PostgresException(
            messageText: "dup",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.UniqueViolation);
        var ef = new DbUpdateException("EF write failed", pg);

        await handler.TryHandleAsync(ctx, ef, CancellationToken.None);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Maps_PostgresForeignKeyViolation_FallsThrough_To_500()
    {
        var (handler, ctx, _) = Build("Production");
        var pg = new PostgresException(
            messageText: "fk",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.ForeignKeyViolation);

        await handler.TryHandleAsync(ctx, pg, CancellationToken.None);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task RequestAborted_ReturnsFalse_NoBodyWritten()
    {
        var (handler, ctx, _) = Build("Production");
        ctx.RequestAborted = new CancellationToken(canceled: true);

        var handled = await handler.TryHandleAsync(ctx, new OperationCanceledException(), CancellationToken.None);

        handled.ShouldBeFalse();
        ctx.Response.Body.Length.ShouldBe(0);
    }

    [Fact]
    public async Task ServerSideOperationCanceled_ViaFrameworkToken_ReturnsFalse()
    {
        var (handler, ctx, _) = Build("Production");
        using var serverCts = new CancellationTokenSource();
        serverCts.Cancel();

        var handled = await handler.TryHandleAsync(ctx, new OperationCanceledException(serverCts.Token), serverCts.Token);

        handled.ShouldBeFalse();
        ctx.Response.Body.Length.ShouldBe(0);
    }

    [Fact]
    public async Task Production_LogEntry_AttachesException_ForTriage()
    {
        var (handler, ctx, recorder) = Build("Production");
        var exception = new InvalidOperationException("boom");

        await handler.TryHandleAsync(ctx, exception, CancellationToken.None);

        recorder.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task NonProduction_LogEntry_AttachesException_ForDevelopmentDebugging()
    {
        var (handler, ctx, recorder) = Build("Development");
        var exception = new InvalidOperationException("dev-detail");

        await handler.TryHandleAsync(ctx, exception, CancellationToken.None);

        recorder.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Instance_OmitsQueryString_DoesNotLeakTokens_InProduction()
    {
        var (handler, ctx, _) = Build("Production");
        ctx.Request.QueryString = new QueryString("?token=abc123&sessionId=xyz");

        await handler.TryHandleAsync(ctx, new InvalidOperationException("boom"), CancellationToken.None);

        var body = await ReadBodyAsync(ctx);
        var instance = body.GetProperty("instance").GetString().ShouldNotBeNull();
        instance.ShouldNotContain("abc123");
        instance.ShouldNotContain("xyz");
    }

    [Fact]
    public async Task Instance_OmitsQueryString_InNonProduction_Too()
    {
        var (handler, ctx, _) = Build("Development");
        ctx.Request.QueryString = new QueryString("?token=abc123");

        await handler.TryHandleAsync(ctx, new InvalidOperationException("boom"), CancellationToken.None);

        var body = await ReadBodyAsync(ctx);
        body.GetProperty("instance").GetString().ShouldNotBeNull().ShouldNotContain("abc123");
    }

    [Fact]
    public async Task BadHttpRequestException_Title_DoesNotEchoMessage_NonProduction()
    {
        var (handler, ctx, _) = Build("Development");
        var ex = new BadHttpRequestException("Request contains malformed Host header: '<script>'", StatusCodes.Status400BadRequest);

        await handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        var body = await ReadBodyAsync(ctx);
        body.GetProperty("title").GetString().ShouldNotBeNull().ShouldNotContain("<script>");
    }

    private static (
        GlobalExceptionHandler handler,
        HttpContext ctx,
        RecordingLogger<GlobalExceptionHandler> log) Build(string environmentName)
    {
        var env = new HostingEnvironment { EnvironmentName = environmentName };
        var log = new RecordingLogger<GlobalExceptionHandler>();
        var handler = new GlobalExceptionHandler(env, log);
        var ctx = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
            TraceIdentifier = "trace-abc",
        };
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/test";
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("localhost");
        return (handler, ctx, log);
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
