namespace PoRepoLineTracker.Api.Middleware;

/// <summary>
/// Middleware that adds security headers to HTTP responses to protect against common web vulnerabilities.
/// Implements headers for:
/// - XSS Protection
/// - Clickjacking Protection (Clickjack attacks)
/// - MIME Sniffing Protection
/// - HSTS (Strict Transport Security)
/// - Referrer Policy
/// - Content Security Policy
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SecurityHeadersMiddleware> _logger;
    private readonly bool _isDevelopment;

    public SecurityHeadersMiddleware(RequestDelegate next, ILogger<SecurityHeadersMiddleware> logger, IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _isDevelopment = environment.IsDevelopment();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // X-Content-Type-Options: Prevents MIME type sniffing attacks
        // Instructs browsers to respect the Content-Type header and not try to detect it
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        // X-Frame-Options: Prevents clickjacking attacks
        // DENY = The page cannot be displayed in a frame (most restrictive)
        // SAMEORIGIN = The page can only be displayed in a frame if the frame origin matches the page origin
        context.Response.Headers["X-Frame-Options"] = "DENY";

        // X-XSS-Protection: Legacy XSS protection (modern browsers use CSP instead)
        // 1; mode=block = Enable XSS filter and block page if attack detected
        context.Response.Headers["X-XSS-Protection"] = "1; mode=block";

        // Referrer-Policy: Controls how much referrer information is shared
        // strict-origin-when-cross-origin: Send origin only on cross-origin requests
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // Content-Security-Policy (CSP): Restricts sources of content that can be loaded
        //
        // script-src used to be "'self' 'unsafe-inline' 'unsafe-eval' https:" — any script from
        // any HTTPS host, plus eval, which is no XSS protection at all. Blazor WASM needs only
        // 'wasm-unsafe-eval' (compile WebAssembly, not eval strings). 'unsafe-inline' stays for
        // the boot scripts in index.html; ponytail: move those to a .js file and drop it.
        // Development keeps the loose form: the debugger/hot-reload use eval and the Scalar API
        // reference loads from a CDN.
        var scriptSrc = _isDevelopment
            ? "script-src 'self' 'unsafe-inline' 'unsafe-eval' https:; "
            : "script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval'; ";
        var cspHeader = "default-src 'self'; " +
                        scriptSrc +
                        "style-src 'self' 'unsafe-inline'; " +    // Radzen sets inline styles
                        "img-src 'self' data: https:; " +    // GitHub avatars
                        "font-src 'self'; " +    // fonts ship with Radzen under _content/
                                                 // wss: is listed explicitly for the /hubs/analysis WebSocket. CSP 3 treats
                                                 // 'self' as covering a same-origin ws/wss upgrade, but that was clarified
                                                 // late and engines disagreed for years — an omitted scheme here fails as a
                                                 // silently blocked connection, so it is named rather than assumed.
                        "connect-src 'self' wss:; " +
                        // Both named rather than left to fall back, for the same reason wss: is.
                        // worker-src falls back through child-src to script-src, and manifest-src
                        // falls back to default-src — so the app is installable today by accident
                        // of those chains rather than by intent. Tightening script-src later (the
                        // obvious next hardening step, since it currently allows https: wholesale)
                        // would silently take the service worker with it, and a blocked worker
                        // fails as "the install button never appears", which is close to
                        // undiagnosable.
                        "worker-src 'self'; " +
                        "manifest-src 'self'; " +
                        "frame-ancestors 'none'; " +
                        "upgrade-insecure-requests;";

        context.Response.Headers["Content-Security-Policy"] = cspHeader;

        // Permissions-Policy (formerly Feature-Policy): Controls browser features
        // Restrict certain APIs to prevent misuse
        context.Response.Headers["Permissions-Policy"] = "geolocation=(), " +
                                                         "microphone=(), " +
                                                         "camera=(), " +
                                                         "payment=()";

        // Strict-Transport-Security (HSTS): Forces HTTPS connections
        // This header is only sent over HTTPS connections
        if (context.Request.IsHttps)
        {
            // max-age: 31536000 = 1 year in seconds
            // includeSubDomains: Apply policy to all subdomains
            // preload: Allow domain to be included in browser HSTS preload lists
            context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains; preload";
        }

        // Additional security headers for additional protection
        // Prevents the browser from prefetching DNS queries
        context.Response.Headers["X-DNS-Prefetch-Control"] = "off";

        _logger.LogDebug("Security headers applied to response for path {Path}", context.Request.Path);

        await _next(context);
    }
}
