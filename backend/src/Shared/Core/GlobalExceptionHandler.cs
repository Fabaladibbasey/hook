using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Hook.Shared.Core;

public sealed class GlobalExceptionHandler(
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException
            && (httpContext.RequestAborted.IsCancellationRequested || cancellationToken.IsCancellationRequested))
            return false;

        var status = MapStatus(exception);

        // Attach the exception object — self-hosted Seq + stdout are
        // operator-controlled, so the log channel is safe to enrich. Redaction
        // happens at the response surface (BuildRedacted), not at the log.
        logger.LogError(exception,
            "Unhandled {ExceptionType} status={Status} path={Path}",
            exception.GetType().FullName, status, httpContext.Request.Path);

        var problem = environment.IsProduction()
            ? BuildRedacted(httpContext, status)
            : BuildVerbose(httpContext, exception, status);

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }

    private static string BuildInstance(HttpRequest req) =>
        $"{req.Scheme}://{req.Host}{req.Path}";

    // Walks the inner-exception + AggregateException tree so EF wraps and
    // Task.WhenAll fan-in aggregates still map to the right status code.
    internal static int MapStatus(Exception exception)
    {
        var stack = new Stack<Exception>();
        stack.Push(exception);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            switch (current)
            {
                case BadHttpRequestException b:
                    return b.StatusCode >= 400 && b.StatusCode < 500 ? b.StatusCode : StatusCodes.Status400BadRequest;
                case JsonException:
                    return StatusCodes.Status400BadRequest;
                case PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }:
                    return StatusCodes.Status409Conflict;
            }
            if (current is AggregateException agg)
            {
                foreach (var inner in agg.Flatten().InnerExceptions)
                    stack.Push(inner);
            }
            else if (current.InnerException is { } inner)
            {
                stack.Push(inner);
            }
        }
        return StatusCodes.Status500InternalServerError;
    }

    // Detail intentionally omits exception.Message — PostgresException, BadHttpRequestException,
    // and others embed PII (phone numbers, raw input bytes, constraint values). The traceId
    // extension is the operator's correlation back to the (operator-controlled) logs.
    private static ProblemDetails BuildVerbose(HttpContext context, Exception exception, int status) => new()
    {
        Title = exception.GetType().Name,
        Detail = $"{exception.GetType().Name} — see traceId in server logs",
        Status = status,
        Instance = BuildInstance(context.Request),
        Extensions =
        {
            ["traceId"] = context.TraceIdentifier,
            ["activityId"] = context.Features.Get<IHttpActivityFeature>()?.Activity.Id,
            ["method"] = context.Request.Method,
            // `path` retained alongside `Instance` for backward compatibility with prior
            // ProblemDetails consumers that key on the extension.
            ["path"] = context.Request.Path.Value,
        },
    };

    private static ProblemDetails BuildRedacted(HttpContext context, int status) => new()
    {
        Title = "An unexpected error occurred.",
        Status = status,
        Instance = BuildInstance(context.Request),
        Extensions =
        {
            ["traceId"] = context.TraceIdentifier,
            ["activityId"] = context.Features.Get<IHttpActivityFeature>()?.Activity.Id,
            ["method"] = context.Request.Method,
            ["path"] = context.Request.Path.Value,
        },
    };
}
