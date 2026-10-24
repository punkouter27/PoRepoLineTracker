using Microsoft.AspNetCore.Antiforgery;

namespace PoRepoLineTracker.API.Features.Antiforgery;

/// <summary>
/// Issues the antiforgery request token the WASM client echoes back on state-changing calls.
/// One endpoint, one job — the validation half lives in <c>AntiforgeryMiddleware</c>.
/// </summary>
internal static class AntiforgeryEndpoints
{
    internal static void MapAntiforgeryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/antiforgery").WithTags("Antiforgery");

        // AllowAnonymous: the token has to be obtainable before the first state-changing call,
        // and it is not a credential — it proves the request came from this origin, not that the
        // caller is anyone in particular. Gating it behind the FallbackPolicy would also make the
        // login POST unreachable.
        group.MapGet("/token", (HttpContext context, IAntiforgery antiforgery) =>
            {
                // GetAndStoreTokens both sets the HttpOnly cookie half and returns the request
                // half. The client cannot read the cookie, which is the point: an attacker's page
                // can cause the cookie to be sent but cannot read this response to match it.
                var tokens = antiforgery.GetAndStoreTokens(context);

                // Never cached: a token handed to one session must not be replayed from a proxy.
                context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                context.Response.Headers.Pragma = "no-cache";

                return Results.Ok(new AntiforgeryTokenResponse
                {
                    Token = tokens.RequestToken ?? string.Empty,
                    HeaderName = tokens.HeaderName ?? "X-CSRF-TOKEN"
                });
            })
            .AllowAnonymous()
            .WithName("GetAntiforgeryToken")
            .WithSummary("Issues an antiforgery request token for state-changing API calls");
    }
}
