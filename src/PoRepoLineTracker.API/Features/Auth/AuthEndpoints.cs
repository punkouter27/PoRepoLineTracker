using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Http.HttpResults;
using Serilog;

namespace PoRepoLineTracker.Api.Features.Auth;

internal static class AuthEndpoints
{
    internal static void MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The whole /auth slice is anonymous by construction: these are the routes a
        // signed-out browser must reach to sign in, so the group opts out of the FallbackPolicy once.
        var auth = endpoints.MapGroup("/auth")
            .WithTags("Auth")
            .AllowAnonymous();

        auth.MapGet("/login", async (
            string? returnUrl,
            bool? dev,
            IConfiguration config,
            IWebHostEnvironment env,
            HttpContext context,
            IUserService userService) =>
        {
            // If in Development and accessed via http port 5000, redirect to https://localhost:5001
            // so the OAuth challenge and correlation cookie align with GitHub's registered callback URL.
            if (env.IsDevelopment() && context.Request.Host.Port == 5000 && (context.Request.Host.Host == "localhost" || context.Request.Host.Host == "127.0.0.1"))
            {
                var query = !string.IsNullOrEmpty(returnUrl) ? $"?returnUrl={Uri.EscapeDataString(returnUrl)}" : "";
                if (dev == true) query += (string.IsNullOrEmpty(query) ? "?dev=true" : "&dev=true");
                return Results.Redirect($"https://localhost:5001/auth/login{query}");
            }

            var ghClientId = env.IsDevelopment()
                ? config[ConfigKeys.GitHub.DevClientId] ?? config[ConfigKeys.GitHub.ClientId]
                : config[ConfigKeys.GitHub.ClientId];
            var ghClientSecret = env.IsDevelopment()
                ? config[ConfigKeys.GitHub.DevClientSecret] ?? config[ConfigKeys.GitHub.ClientSecret]
                : config[ConfigKeys.GitHub.ClientSecret];

            var isOAuthConfigured = !string.IsNullOrEmpty(ghClientId) && !string.IsNullOrEmpty(ghClientSecret);

            // Local paths only. The value arrives in the query string, so without this a link to
            // /auth/login?returnUrl=https://evil.example signs the user in with GitHub and then
            // forwards them — still on a trusted-looking flow — to the attacker's page. The dev
            // bypass below always checked; the real OAuth path did not.
            // IsLocalUrl also accepts app-relative "~/x", which neither Redirect nor RedirectUri
            // resolves — the browser would land on /auth/~/x — so require a leading '/'.
            returnUrl = RedirectHttpResult.IsLocalUrl(returnUrl) && returnUrl![0] == '/' ? returnUrl : "/";
            var isDevBypass = env.IsDevelopment() && (!isOAuthConfigured || dev == true);

            if (isDevBypass)
            {
                var devUserId = new UserId(new Guid("00000000-0000-0000-0000-000000000001"));
                var username = "DevUser";
                var displayName = "Local Developer";
                var email = "dev@localhost";

                try
                {
                    var savedUser = await userService.UpsertUserAsync(new User
                    {
                        Id = devUserId,
                        GitHubId = "dev",
                        Username = username,
                        DisplayName = displayName,
                        Email = email,
                        AvatarUrl = string.Empty,
                        AccessToken = config[ConfigKeys.GitHub.Pat] ?? string.Empty
                    });
                    devUserId = savedUser.Id;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Storage unavailable during dev sign-in; claims fallback will be used");
                }

                var claims = new List<Claim>
                {
                    new(ClaimsPrincipalExtensions.UserIdClaim, devUserId.ToString()),
                    new(ClaimTypes.NameIdentifier, devUserId.ToString()),
                    new(ClaimTypes.Name, username),
                    new("DisplayName", displayName),
                    new(ClaimTypes.Email, email),
                    new("AvatarUrl", string.Empty)
                };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);

                await context.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    principal,
                    new AuthenticationProperties
                    {
                        IsPersistent = true,
                        RedirectUri = returnUrl
                    });

                return Results.Redirect(returnUrl);
            }

            if (isOAuthConfigured)
            {
                return Results.Challenge(
                    new AuthenticationProperties { RedirectUri = returnUrl },
                    [GitHubAuthenticationDefaults.AuthenticationScheme]);
            }

            return Results.Problem(
                title: "No OAuth provider configured",
                detail: "GitHub OAuth is not configured. Set GitHub:ClientId and GitHub:ClientSecret in configuration (user-secrets, appsettings.Development.local.json, or Key Vault).",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        })
        .WithName("Login");

        // The /auth/login/microsoft route is gone with the Microsoft provider — see
        // AuthServiceExtensions for why. Left as a comment rather than a 410 handler because
        // nothing links to it: the login page's second button was removed in the same change.

        auth.MapGet("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            // Redirect to /login so the user lands on the unauthenticated landing
            // page even if their cookie had already expired (avoids a stale-session
            // 302 loop where the auth filter keeps bouncing them).
            return Results.Redirect("/login");
        })
        .WithName("Logout");

        // Anonymous by design: the Blazor client polls this to discover whether it has a
        // session, and must get a well-formed "not authenticated" answer rather than a 401.
        auth.MapGet("/me", async (HttpContext context, IUserService userService) =>
        {
            if (context.User.Identity?.IsAuthenticated != true)
                return Results.Ok(new AuthResponse(IsAuthenticated: false));

            if (!context.User.TryGetUserId(out var userId))
                return Results.Ok(new AuthResponse(IsAuthenticated: false));

            try
            {
                var user = await userService.GetUserByIdAsync(userId);
                if (user == null)
                {
                    // User not yet persisted (e.g. storage was unavailable during the OAuth
                    // callback). Fall back to claims so the session stays authenticated.
                    Log.Debug("User {UserId} not found in storage; falling back to claims", userId);
                    return Results.Ok(new AuthResponse(
                        IsAuthenticated: true,
                        UserId: userId.ToString(),
                        Username: context.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "User",
                        DisplayName: context.User.FindFirst("DisplayName")?.Value,
                        Email: context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
                        AvatarUrl: context.User.FindFirst("AvatarUrl")?.Value ?? ""));
                }

                return Results.Ok(new AuthResponse(
                    IsAuthenticated: true,
                    UserId: user.Id.ToString(),
                    Username: user.Username,
                    DisplayName: user.DisplayName,
                    Email: user.Email,
                    AvatarUrl: user.AvatarUrl));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "User service unavailable in GetCurrentUser, using claims fallback");
                return Results.Ok(new AuthResponse(
                    IsAuthenticated: true,
                    UserId: userId.ToString(),
                    Username: context.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "User",
                    DisplayName: context.User.FindFirst("DisplayName")?.Value,
                    Email: context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
                    AvatarUrl: context.User.FindFirst("AvatarUrl")?.Value ?? ""));
            }
        })
        .WithName("GetCurrentUser");
    }
}
