using Serilog.Context;

namespace Hook.Features.Observability;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var id = context.Request.Headers.TryGetValue(HeaderName, out var supplied)
                 && !string.IsNullOrWhiteSpace(supplied)
            ? supplied.ToString()
            : Guid.NewGuid().ToString("N");

        context.Response.Headers[HeaderName] = id;
        context.TraceIdentifier = id;

        using (LogContext.PushProperty("CorrelationId", id))
        {
            await next(context);
        }
    }
}
