using Serilog.Context;
using System.Security.Claims;

namespace PoRepoLineTracker.Api.Middleware;

/// <summary>
/// Middleware that gives each HTTP request a correlation ID for distributed tracing.
/// The UserId property is pushed separately, after UseAuthentication (see Program.cs) — this
/// middleware runs first in the pipeline, before there is a principal to read it from.
/// </summary>
public class LogEnrichmentMiddleware
{
    private readonly RequestDelegate _next;

    public LogEnrichmentMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Generate or retrieve correlation ID for this request
        var correlationId = context.Request.Headers.TryGetValue("X-Correlation-ID", out var correlationIdHeader)
            ? correlationIdHeader.ToString()
            : Guid.NewGuid().ToString("N");

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            // Add correlation ID to response headers for client-side tracing
            context.Response.Headers["X-Correlation-ID"] = correlationId;

            await _next(context);
        }
    }
}
