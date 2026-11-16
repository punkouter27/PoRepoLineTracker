namespace PoRepoLineTracker.Api.Middleware;

/// <summary>
/// Answers 404 (JSON) for an <c>/api</c> path that no API route claimed.
///
/// <para><b>The problem.</b> The Blazor shell is served by a fallback endpoint matching
/// <c>{**path:nonfile}</c>, which happily matches <c>/api/typo</c> too. An API caller that
/// misspelled a route therefore got <b>200 with an HTML document</b> — which fails later, somewhere
/// else, in whatever way that caller happens to parse it, instead of saying "no such route". In
/// Production it was worse: the auth gate answered 401 for the whole <c>/api</c> prefix before
/// routing ran, so a typo was indistinguishable from an expired session.</para>
///
/// <para><b>Why middleware and not a catch-all route.</b> A <c>MapFallback("/api/{**rest}")</c>
/// endpoint was tried first and reverted. It becomes a routing candidate next to the real routes,
/// and it won for some of them — <c>POST /api/repositories/bulk</c> started matching the catch-all
/// rather than the bulk endpoint, and because a catch-all like that must be anonymous, an
/// unauthenticated write sailed through authorization and was refused by the antiforgery gate with
/// a 400 instead of the 401 it should get. Deciding this outside routing removes the competition
/// entirely: nothing here can shadow an endpoint, because it is not an endpoint.</para>
///
/// <para><b>Placement.</b> After routing (so the match is known) and before
/// <c>UseAuthorization</c> — otherwise the authorization FallbackPolicy answers 401 for an
/// unmatched path first, which is the very confusion this exists to remove.</para>
///
/// <para><b>Known limit: this fixes GET, not every verb.</b> An unknown <c>/api</c> path reached
/// by GET answers 404. The same path reached by POST/PUT/DELETE still answers 401, because those
/// verbs resolve to something other than the SPA fallback before this runs and the authorization
/// challenge wins. That is deliberate scope, not an oversight: GET is the case that was actively
/// broken (200 carrying an HTML document to a JSON caller), and the alternative — a catch-all
/// endpoint covering every verb — is exactly what was tried and reverted for shadowing real
/// routes. A write to a route that does not exist being refused as unauthorized is a poor error
/// message; a write to a route that DOES exist being refused for the wrong reason is a bug.</para>
/// </summary>
public sealed class ApiNotFoundMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsUnroutedApiRequest(context))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new ErrorResponse
        {
            Title = "No such route",
            Detail = $"{context.Request.Method} {context.Request.Path} is not an API route on this server.",
            Code = "route_not_found",
            Status = StatusCodes.Status404NotFound
        }, AppJsonSerializerContext.Default.ErrorResponse);
    }

    private static bool IsUnroutedApiRequest(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            return false;

        var endpoint = context.GetEndpoint();

        // Nothing matched at all, or the only thing that matched was the SPA shell fallback —
        // which is the case that produced 200-with-HTML. Both mean "no API route claimed this".
        return endpoint is null || endpoint.Metadata.GetMetadata<SpaFallbackAttribute>() is not null;
    }
}

/// <summary>
/// Marks the Blazor shell's fallback endpoint so <see cref="ApiNotFoundMiddleware"/> can recognise
/// it. A marker rather than a display-name comparison: the display name is a framework-generated
/// string that no test would notice changing.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SpaFallbackAttribute : Attribute;
