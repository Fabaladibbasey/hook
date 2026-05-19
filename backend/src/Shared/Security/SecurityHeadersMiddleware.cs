using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Hook.Shared.Security;

internal static class SecurityHeadersMiddleware
{
    internal const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "object-src 'none'; " +
        "upgrade-insecure-requests";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, IHostEnvironment environment)
    {
        var emitHsts = !environment.IsDevelopment();
        return app.Use((ctx, next) =>
        {
            // OnStarting survives ExceptionHandlerMiddleware.Response.Clear() — eagerly written
            // headers would be wiped on the exception re-run path.
            ctx.Response.OnStarting(static state =>
            {
                var (response, hsts) = ((HttpResponse, bool))state;
                WriteHeaders(response.Headers, hsts);
                return Task.CompletedTask;
            }, (ctx.Response, emitHsts));
            return next(ctx);
        });
    }

    internal static void WriteHeaders(IHeaderDictionary headers, bool emitHsts)
    {
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), interest-cohort=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        if (emitHsts)
        {
            headers["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains; preload";
        }
    }
}
