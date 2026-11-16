using PoRepoLineTracker.Api.Features.Antiforgery;
using PoRepoLineTracker.Api.Features.Auth;
using PoRepoLineTracker.Api.Features.CodeHealth;
using PoRepoLineTracker.Api.Features.Contributors;
using PoRepoLineTracker.Api.Features.Dev;
using PoRepoLineTracker.Api.Features.Diagnostics;
using PoRepoLineTracker.Api.Features.GitHub;
using PoRepoLineTracker.Api.Features.Insights;
using PoRepoLineTracker.Api.Features.Repositories;
using PoRepoLineTracker.Api.Features.Settings;

namespace PoRepoLineTracker.Api.Extensions;

/// <summary>
/// GoF Facade Pattern: Provides a unified interface to register all API endpoint groups.
/// Each endpoint group (Auth, Repositories, GitHub, Settings, Diagnostics)
/// lives in its own file following the VSA (Vertical Slice Architecture) principle.
///
/// SOLID — Interface Segregation Principle: Each endpoint module has its own mapping method,
/// keeping the API surface organized and testable.
///
/// Every module maps onto an <see cref="IEndpointRouteBuilder"/> and builds its
/// routes under a <c>MapGroup()</c>, so the route prefix, tags, and authorization policy for a
/// slice are declared once instead of repeated on every endpoint.
/// </summary>
public static class ApiEndpointExtensions
{
    public static WebApplication MapApiEndpoints(this WebApplication app)
    {
        var isDevOrTest = Auth.FakeAuthHandler.IsAllowedIn(app.Environment);

        app.MapAntiforgeryEndpoints();
        app.MapAuthEndpoints();
        app.MapRepositoryEndpoints();
        app.MapPortfolioExportEndpoints();
        app.MapAccountEndpoints();
        app.MapPublicBadgeEndpoints();
        app.MapWebhookEndpoints();
        app.MapSettingsEndpoints();
        app.MapGitHubEndpoints();
        app.MapDiagnosticsEndpoints();
        app.MapContributorEndpoints();
        app.MapInsightsEndpoints();
        app.MapCodeHealthEndpoints();

        if (isDevOrTest)
        {
            // Writes arbitrary history to the caller's account, so it is gated exactly like
            // FakeAuthHandler — see SeedEndpoints for why the UI tier needs it.
            app.MapSeedEndpoints();
        }

        // NOTE: there is deliberately no catch-all ENDPOINT for unmatched /api paths.
        //
        // The obvious `MapFallback("/api/{**rest}", …)` was tried and reverted: a catch-all
        // registered as an endpoint becomes a routing candidate alongside the real routes, and it
        // won often enough to matter — `POST /api/repositories/bulk` began resolving to the
        // catch-all, so an anonymous write passed authorization (the catch-all has to be
        // anonymous) and came back 400 from the antiforgery gate instead of the 401 it owes the
        // caller. Route precedence is not something to bet an authorization outcome on.
        //
        // ApiNotFoundMiddleware does the same job from outside routing, where it cannot shadow
        // anything — see Program.cs.

        return app;
    }
}
