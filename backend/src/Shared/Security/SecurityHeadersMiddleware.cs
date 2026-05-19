using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Hook.Shared.Security;

/// <summary>
/// Writes a baseline set of response security headers on every response. Replaces the
/// inline Referrer-Policy lambda — keeps the header surface in one auditable file.
/// CSP <c>connect-src wss: https:</c> permits the SignalR hub at /hubs/chat;
/// <c>style-src 'unsafe-inline'</c> accommodates Tailwind's inline style emission.
/// HSTS only emitted outside Development since Kestrel dev is plain HTTP.
/// </summary>
internal static class SecurityHeadersMiddleware
{
    internal const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self' wss: https:; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "object-src 'none'; " +
        "upgrade-insecure-requests";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, IHostEnvironment environment)
    {
        var emitHsts = !environment.IsDevelopment();
        return app.Use(async (ctx, next) =>
        {
            WriteHeaders(ctx.Response.Headers, emitHsts);
            await next();
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
