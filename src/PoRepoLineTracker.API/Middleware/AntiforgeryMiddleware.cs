using Microsoft.AspNetCore.Antiforgery;

namespace PoRepoLineTracker.Api.Middleware;

/// <summary>
/// Validates the antiforgery token on every state-changing API request.
///
/// <para><b>Why hand-rolled rather than <c>app.UseAntiforgery()</c>.</b> The framework middleware
/// only validates endpoints whose metadata asks for it, and that metadata is added automatically
/// just for endpoints that bind a form. Every state-changing endpoint here takes JSON, so
/// <c>UseAntiforgery()</c> alone would pass all of them through unvalidated — the rule would be
/// satisfied on paper and enforce nothing. This middleware inverts the default: unsafe methods
/// are validated unless a slice explicitly opts out.</para>
///
/// <para><b>Placement.</b> Registered after <c>UseAuthorization()</c> so an unauthenticated caller
/// still gets 401 rather than 400 — a missing credential is the more useful diagnosis, and the
/// API tier asserts exactly that. CSRF is only meaningful once a caller is carrying a cookie
/// anyway.</para>
///
/// <para><b>Scope.</b> Only <c>/api</c>. The OAuth callback routes are outside it and must stay
/// unvalidated: the provider POSTs back cross-site by design and has no token to present.</para>
/// </summary>
public sealed class AntiforgeryMiddleware(
    RequestDelegate next,
    IAntiforgery antiforgery,
    ILogger<AntiforgeryMiddleware> logger)
{
    /// <summary>Methods RFC 9110 defines as safe — no state change, so nothing to forge.</summary>
    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresValidation(context))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException ex)
            {
                // Deliberately terse to the caller: echoing the validation detail tells an
                // attacker which half of the double-submit failed. The detail goes to the log.
                logger.LogWarning(ex,
                    "Antiforgery validation failed for {Method} {Path}",
                    context.Request.Method, context.Request.Path);

                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new ErrorResponse
                {
                    Title = "Invalid antiforgery token",
                    Detail = "The request did not carry a valid antiforgery token. Fetch one from /api/antiforgery/token and resend it in the X-CSRF-TOKEN header.",
                    Code = "antiforgery_validation_failed",
                    Status = StatusCodes.Status400BadRequest
                }, AppJsonSerializerContext.Default.ErrorResponse);
                return;
            }
        }

        await next(context);
    }

    private static bool RequiresValidation(HttpContext context)
    {
        if (SafeMethods.Contains(context.Request.Method)) return false;
        if (!context.Request.Path.StartsWithSegments("/api")) return false;

        // An endpoint may opt out for a documented reason (see SkipAntiforgeryAttribute).
        return context.GetEndpoint()?.Metadata.GetMetadata<SkipAntiforgeryAttribute>() is null;
    }
}

/// <summary>
/// Marks an endpoint as exempt from <see cref="AntiforgeryMiddleware"/>.
///
/// Applied with <c>.WithMetadata(new SkipAntiforgeryAttribute("reason"))</c>. The reason is
/// required rather than optional so an exemption cannot be added silently — every use of this
/// attribute is a hole in the antiforgery guarantee and should read as one.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class SkipAntiforgeryAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
