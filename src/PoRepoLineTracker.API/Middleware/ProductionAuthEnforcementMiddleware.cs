namespace PoRepoLineTracker.API.Middleware;

/// <summary>
/// Production sign-in gate for PAGE navigations.
///
/// <para>In Production an unauthenticated browser asking for an app route is sent to the app's own
/// <c>/login</c> page. In Development and Test this is a no-op, so local runs and the E2E tiers
/// work unauthenticated — which is also why nothing in those tiers exercises the behaviour below,
/// and why it is unit-tested directly.</para>
///
/// <para><b>It redirects to /login, not to the OAuth provider.</b> It used to call
/// <c>ChallengeAsync</c>, which sent the browser straight to
/// <c>github.com/login/oauth/authorize</c>. Two things were wrong with that. The branded
/// <c>/login</c> page — which exists, and is the only place that explains what the app wants
/// access to — was never seen by anyone. And the installed PWA declares
/// <c>start_url: "/"</c>, so launching it navigated OUT of the app's scope on the first request,
/// which breaks the standalone window and any offline start. Sign-in still ends at GitHub; the
/// user gets there by pressing the button on <c>/login</c>, which hits <c>/auth/login</c>.</para>
///
/// <para><b>It does not touch /api.</b> Those routes are already deny-by-default through the
/// authorization FallbackPolicy, and the cookie handler's <c>OnRedirectToLogin</c> already turns an
/// unauthenticated API request into a 401 rather than a redirect. Blanket-401ing the whole prefix
/// here ran *before* routing, so it could not tell a real endpoint from a typo and answered 401 for
/// both — a misspelled client URL surfaced as "you are logged out" instead of "no such route".
/// Letting the request through to routing means the catch-all in ApiEndpointExtensions can answer
/// 404.</para>
/// </summary>
public class ProductionAuthEnforcementMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ProductionAuthEnforcementMiddleware> _logger;

    /// <summary>
    /// Page paths reachable without signing in.
    ///
    /// <para>Short, because it no longer has to mirror the static assets. This middleware is
    /// registered AFTER <c>UseStaticFiles</c>, so every physical file — the framework payload,
    /// css, icons, the manifest, the service worker — is already served and short-circuited long
    /// before the request arrives here. The list used to name them anyway, which read as though
    /// forgetting one would gate an asset; it would not, and the entries were dead. What is left
    /// is the set of real endpoints that must answer anonymously.</para>
    /// </summary>
    private static readonly string[] PublicPaths =
    [
        "/health",
        "/auth",   // covers /auth/login, /auth/logout, /auth/me and the OAuth callback
        "/login",  // the page this middleware redirects TO — gating it would be an infinite loop
        "/signin-github",
        "/signout-github",
        // Anonymous by design, for repositories their owner made public (PublicBadgeEndpoints):
        // a README badge fetched through GitHub's image proxy cannot follow a sign-in redirect.
        "/badge",
        "/share"
    ];

    public ProductionAuthEnforcementMiddleware(
        RequestDelegate next,
        IWebHostEnvironment env,
        ILogger<ProductionAuthEnforcementMiddleware> logger)
    {
        _next = next;
        _env = env;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_env.IsProduction() || ShouldPassThrough(context))
        {
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        var target = context.Request.Path + context.Request.QueryString;
        _logger.LogInformation(
            "Production auth enforcement: unauthenticated request to {Path} — sending to /login", context.Request.Path);

        // returnUrl is a path on this origin, not a full URI, and /auth/login hands it to the
        // OAuth properties as-is. Keeping it relative is what stops it being usable as an open
        // redirect to somebody else's host after a successful sign-in.
        context.Response.Redirect($"/login?returnUrl={Uri.EscapeDataString(target)}");
    }

    private static bool ShouldPassThrough(HttpContext context)
    {
        var path = context.Request.Path;

        // Left to routing and the FallbackPolicy — see the type remarks.
        if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) return true;

        // SignalR negotiates over a path the browser cannot follow a redirect on; the hub carries
        // [Authorize] and rejects an unauthenticated connection itself.
        if (path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)) return true;

        // Segment-aware: "/login" matches "/login" and "/login/x" but NOT "/loginfoo". A raw
        // StartsWith would let "/loginfoo" walk straight past the gate.
        foreach (var publicPath in PublicPaths)
        {
            if (path.StartsWithSegments(publicPath, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
