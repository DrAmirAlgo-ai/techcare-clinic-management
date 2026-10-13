using Serilog.Context;

namespace TechCare.Clinic.Api.Infrastructure;

/// <summary>
/// PLAN_ADDENDUM.md 7.6 + 7.7: every response carries the baseline security headers and a correlation id. The id
/// goes back to the client as X-Request-Id and into every log line of that request (as a Serilog scope), so the
/// log file and the client can always be tied to the same request.
///
/// The Content-Security-Policy is applied in production only: the Vite development server injects module scripts
/// and the HMR websocket, and a strict policy would break development. In production the SPA is served from this
/// same origin (behind the reverse proxy), so 'self' is all it needs.
/// </summary>
public class SecurityHeadersMiddleware
{
    private const string StrictCsp =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self' ws: wss:; " +
        "object-src 'none'; base-uri 'self'";

    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        ApplyHeaders(context);

        var correlationId = context.TraceIdentifier;
        context.Response.Headers["X-Request-Id"] = correlationId;

        using (_ = LogContext.PushProperty("CorrelationId", correlationId))
            await _next(context);
    }

    /// <summary>
    /// The baseline security headers, on their own so the exception handler can re-apply them after
    /// <c>Response.Clear()</c>: a 500 must not arrive stripped of the very headers that protect it.
    /// </summary>
    public static void ApplyHeaders(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["X-Frame-Options"] = "DENY";
        // PLAN_ADDENDUM.md 7.6: the microphone stays available to the clinic's own pages (the dictation feature,
        // PLAN_FULL.md 5.2); everything else is closed.
        headers["Permissions-Policy"] = "microphone=(self), camera=(), geolocation=()";
        if (context.RequestServices.GetRequiredService<IHostEnvironment>().IsProduction())
            headers["Content-Security-Policy"] = StrictCsp;
    }
}