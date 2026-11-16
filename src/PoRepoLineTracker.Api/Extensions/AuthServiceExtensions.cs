using PoRepoLineTracker.Api.Platform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;

namespace PoRepoLineTracker.Api.Extensions;

public static class AuthServiceExtensions
{
    /// <summary>
    /// Non-Production default scheme: forwards to <see cref="FakeAuthHandler"/> when the request
    /// carries <c>X-Fake-User</c>, otherwise to the cookie scheme.
    /// </summary>
    internal const string SmartAuthScheme = "FakeOrCookie";

    public static IServiceCollection AddAuth(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var keysDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PoRepoLineTracker",
            "dataprotection-keys");
        services.AddDataProtection()
            .SetApplicationName("PoRepoLineTracker")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        // GitHub is the only OAuth provider (see the note further down for why Microsoft was
        // removed). If it is not configured — local dev without secrets — fall back to the cookie
        // scheme so GUEST mode and other non-OAuth flows still work rather than throwing at boot.
        //
        // In Development, prefer the Dev-only OAuth App (Key Vault: GitHub:Dev:ClientId) over the
        // production one: a GitHub OAuth App has exactly one callback URL, and the production app's
        // callback is the deployed site, not localhost — so a developer's machine needs its own App
        // registration rather than sharing the production credential. Falls back to the production
        // key so a Development run still boots (with GitHub sign-in unusable) if the Dev secret
        // hasn't been set.
        var ghClientId = environment.IsDevelopment()
            ? configuration[ConfigKeys.GitHub.DevClientId] ?? configuration[ConfigKeys.GitHub.ClientId]
            : configuration[ConfigKeys.GitHub.ClientId];
        var ghClientSecret = environment.IsDevelopment()
            ? configuration[ConfigKeys.GitHub.DevClientSecret] ?? configuration[ConfigKeys.GitHub.ClientSecret]
            : configuration[ConfigKeys.GitHub.ClientSecret];

        var isGitHubConfigured = !string.IsNullOrEmpty(ghClientId) && !string.IsNullOrEmpty(ghClientSecret);

        var defaultChallengeScheme = isGitHubConfigured
            ? GitHubAuthenticationDefaults.AuthenticationScheme
            : CookieAuthenticationDefaults.AuthenticationScheme;

        // In Development and Test the default scheme is a policy scheme that forwards to
        // FakeAuthHandler when X-Fake-User is present and to the cookie otherwise. Selecting here
        // (rather than naming schemes on the authorization policies) leaves FallbackPolicy
        // scheme-agnostic, so it keeps working under any DefaultScheme.
        var useFakeAuth = FakeAuthHandler.IsAllowedIn(environment);
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = useFakeAuth
                ? SmartAuthScheme
                : CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = defaultChallengeScheme;
        })
        .AddCookie(options =>
        {
            // The prefix is only legal on a Secure cookie, so it tracks the SecurePolicy below.
            options.Cookie.Name = PoPlatform.SessionCookieName(secure: !environment.IsDevelopment());
            options.Cookie.HttpOnly = true;
            // SameSite=Lax (not Strict): after returning from an external OAuth provider the
            // browser withholds a freshly-set Strict cookie on the post-login landing redirect,
            // causing an infinite "pick an account" loop. Lax sends the cookie on the top-level
            // GET navigation while still blocking cross-site POST CSRF — the correct setting for
            // an interactive OAuth session cookie. (The house rule names Strict, but Strict is
            // incompatible with the OAuth sign-in redirect.)
            options.Cookie.SameSite = SameSiteMode.Lax;
            // Use SameAsRequest so cookies work over plain HTTP on localhost
            options.Cookie.SecurePolicy = environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
            options.LoginPath = "/auth/login";
            options.LogoutPath = "/auth/logout";
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }
                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }
                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
        });

        // Dev/Test header-driven auth. ThrowIfProduction turns a misconfigured deploy
        // into a startup crash rather than a silent authentication bypass.
        if (useFakeAuth)
        {
            FakeAuthHandler.ThrowIfProduction(environment);
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, FakeAuthHandler>(FakeAuthHandler.SchemeName, _ => { })
                .AddPolicyScheme(SmartAuthScheme, SmartAuthScheme, options =>
                {
                    options.ForwardDefaultSelector = context =>
                        context.Request.Headers.ContainsKey(FakeAuthHandler.UserHeader)
                            ? FakeAuthHandler.SchemeName
                            : CookieAuthenticationDefaults.AuthenticationScheme;
                });
        }

        // GitHub OAuth — only registered when BOTH ClientId and ClientSecret are configured.
        // SOLID — OCP: conditional registration without modifying other providers.
        if (isGitHubConfigured)
        {
            services.AddAuthentication().AddGitHub(options =>
            {
                options.ClientId = ghClientId!;
                options.ClientSecret = ghClientSecret!;
                options.CallbackPath = configuration[ConfigKeys.GitHub.CallbackPath] ?? "/signin-github";

                // Use SameAsRequest so cookies work over plain HTTP on localhost
                options.CorrelationCookie.SecurePolicy = environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.HttpOnly = true;

                options.Scope.Add("user:email");
                options.Scope.Add("read:user");
                options.Scope.Add("repo");
                // No SaveTokens: nothing reads the token back out of the cookie (analysis uses
                // the copy in the Users table), so saving it only put a second copy of a live
                // credential in every browser.

                options.Events.OnRemoteFailure = context =>
                {
                    context.Response.Redirect("/?error=auth_failed");
                    context.HandleResponse();
                    return Task.CompletedTask;
                };

                // Return 401 for API fetch calls instead of redirecting to GitHub OAuth,
                // which would cause a browser CORS error on the cross-origin redirect.
                // /auth/login is a top-level navigation that intentionally challenges to GitHub;
                // it is not under /api, so only XHR /api/* calls get the 401 short-circuit.
                options.Events.OnRedirectToAuthorizationEndpoint = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }
                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };

                options.Events.OnCreatingTicket = async context =>
                {
                    var gitHubId = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    var username = context.Principal?.FindFirst(ClaimTypes.Name)?.Value;
                    var displayName = context.Principal?.FindFirst(GitHubAuthenticationConstants.Claims.Name)?.Value
                                   ?? context.Principal?.FindFirst(ClaimTypes.GivenName)?.Value;
                    var email = context.Principal?.FindFirst(ClaimTypes.Email)?.Value;
                    var avatarUrl = context.User.GetProperty("avatar_url").GetString();
                    var accessToken = context.AccessToken;

                    if (gitHubId != null && username != null && accessToken != null)
                    {
                        try
                        {
                            var userService = context.HttpContext.RequestServices.GetRequiredService<IUserService>();
                            var savedUser = await userService.UpsertUserAsync(new User
                            {
                                GitHubId = gitHubId,
                                Username = username,
                                DisplayName = displayName ?? username,
                                Email = email,
                                AvatarUrl = avatarUrl ?? string.Empty,
                                AccessToken = accessToken
                            });
                            (context.Principal?.Identity as ClaimsIdentity)?.AddClaim(
                                new Claim("UserId", savedUser.Id.ToString()));
                        }
                        catch (Exception ex)
                        {
                            // Storage not available. Fail the sign-in (OnRemoteFailure sends the
                            // browser back with ?error=auth_failed) rather than what this used to
                            // do: mint a random UserId. That session owned no user row and no
                            // token, could see none of the user's data, and every later sign-in
                            // got a different identity.
                            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                            logger.LogError(ex, "Failed to upsert user {GitHubId}/{Username} during GitHub OAuth callback — sign-in refused.", gitHubId, username);
                            throw;
                        }
                    }
                };
            });
        }

        // Microsoft/Entra OAuth was removed here. It is a deliberate, recorded deviation from
        // the house rules — see SPEC.md §9.
        //
        // The short version: this app's entire purpose is reading GitHub repositories, and a
        // Microsoft-authenticated principal has no GitHub credential. The handler used to store
        // the Microsoft access token in User.AccessToken — a field documented as "GitHub OAuth
        // access token ... used to access GitHub API on behalf of the user" — and a synthetic
        // "ms:{oid}" string in User.GitHubId. Every GitHub call then sent a Microsoft token to
        // api.github.com and got a 401, so "Select from GitHub" failed for those users with no
        // explanation. A second sign-in button that cannot reach the app's only data source is
        // worse than no second button.

        // Server-side FallbackPolicy: every endpoint that carries no authorization
        // metadata of its own is authenticated by default. Endpoints that must stay public
        // (/auth/*, /health, the Blazor fallback file, OpenAPI/Scalar in
        // Development) opt out explicitly with .AllowAnonymous(). Deny-by-default means a new
        // endpoint added without a RequireAuthorization() call fails closed, not open.
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });
        return services;
    }
}
