using Hook.Features.Observability;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Shouldly;

namespace Hook.UnitTests.Observability;

public class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task GeneratesIdWhenHeaderMissing()
    {
        var ctx = new DefaultHttpContext();
        var sut = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(ctx);

        var id = ctx.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString();
        id.ShouldNotBeNullOrWhiteSpace();
        id.Length.ShouldBeGreaterThan(16);
        ctx.TraceIdentifier.ShouldBe(id);
    }

    [Fact]
    public async Task PreservesSuppliedHeader()
    {
        var supplied = "abc-123";
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[CorrelationIdMiddleware.HeaderName] = supplied;
        var sut = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(ctx);

        ctx.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString().ShouldBe(supplied);
        ctx.TraceIdentifier.ShouldBe(supplied);
    }

    [Fact]
    public async Task PushesCorrelationIdToLogContext()
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var prior = Log.Logger;
        Log.Logger = logger;

        try
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers[CorrelationIdMiddleware.HeaderName] = "trace-xyz";
            var sut = new CorrelationIdMiddleware(_ =>
            {
                Log.Information("inside");
                return Task.CompletedTask;
            });

            await sut.InvokeAsync(ctx);
        }
        finally
        {
            Log.Logger = prior;
            logger.Dispose();
        }

        sink.Events.ShouldHaveSingleItem();
        sink.Events[0].Properties["CorrelationId"].ToString().Trim('"').ShouldBe("trace-xyz");
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
