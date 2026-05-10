using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

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
        logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);

        var problem = environment.IsProduction()
            ? BuildRedacted(httpContext)
            : BuildVerbose(httpContext, exception);

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }

    private static ProblemDetails BuildVerbose(HttpContext context, Exception exception) => new()
    {
        Title = exception.Message,
        Detail = $"{exception.GetType().FullName}: {exception.Message}",
        Status = StatusCodes.Status500InternalServerError,
        Instance = context.Request.GetDisplayUrl(),
        Extensions =
        {
            ["traceId"] = context.TraceIdentifier,
            ["activityId"] = context.Features.Get<IHttpActivityFeature>()?.Activity.Id,
            ["method"] = context.Request.Method,
            ["path"] = context.Request.Path.ToString(),
            ["queryString"] = RequestLogScrub.Scrub(context.Request.QueryString.ToString()),
        },
    };

    private static ProblemDetails BuildRedacted(HttpContext context) => new()
    {
        Title = "An unexpected error occurred.",
        Status = StatusCodes.Status500InternalServerError,
        Instance = context.Request.GetDisplayUrl(),
        Extensions =
        {
            ["traceId"] = context.TraceIdentifier,
            ["activityId"] = context.Features.Get<IHttpActivityFeature>()?.Activity.Id,
            ["method"] = context.Request.Method,
            ["path"] = context.Request.Path.ToString(),
        },
    };
}
