using System.Net;

namespace PoRepoLineTracker.Api.Features.CodeHealth;

internal static class CodeHealthEndpoints
{
    internal static void MapCodeHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var health = endpoints.MapGroup("/api/code-health")
            .WithTags("CodeHealth")
            .RequireAuthorization();

        // The portfolio list. No path segment, so it cannot be confused with the {repositoryId}
        // route below - and no ownership guard is needed because it never takes an id from the
        // caller: it returns exactly the repositories the signed-in user owns.
        health.MapGet("/", async (HttpContext ctx, GetPortfolioCodeHealthQueryHandler portfolioHandler) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var summaries = await portfolioHandler.Handle(new GetPortfolioCodeHealthQuery(userId));
            return Results.Ok(summaries);
        })
        .WithName("GetPortfolioCodeHealth")
        .WithSummary("Every owned repository scored, worst first, so they can be ranked against each other");

        // Unlike the portfolio routes, this one takes a repository id from the URL — so it needs
        // the ownership guard. Without it any signed-in caller could read a report (paths,
        // hotspots, file sizes) for someone else's private repository.
        health.MapGet("/{repositoryId}", async (RepositoryId repositoryId, HttpContext ctx, GetCodeHealthQueryHandler codeHealthHandler, IRepositoryDataService repoDataService) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var (_, error) = await RepositoryOwnership.AuthorizeAsync(repoDataService, repositoryId, userId, "read code health for");
            if (error != null) return error;

            var report = await codeHealthHandler.Handle(new GetCodeHealthQuery(repositoryId));

            // Null means the repository has never been analysed. A 404 rather than an empty
            // report: a page of zeroes is indistinguishable from genuinely terrible code, and
            // the client renders a "run an analysis first" state off this.
            return report is null
                ? Results.NotFound(new ErrorResponse
                {
                    Title = "Nothing to measure",
                    Detail = "This repository has no analysed commits yet. Run an analysis and try again.",
                    Code = "no_analysed_commits",
                    Status = (int)HttpStatusCode.NotFound
                })
                : Results.Ok(report);
        })
        .WithName("GetCodeHealth");

        // The monthly trend. Same ownership guard as the report above — it is the same data, only
        // measured at more commits, so it leaks exactly as much if left open.
        health.MapGet("/{repositoryId}/trend", async (RepositoryId repositoryId, HttpContext ctx, GetCodeHealthTrendQueryHandler trendHandler, IRepositoryDataService repoDataService) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var (_, error) = await RepositoryOwnership.AuthorizeAsync(repoDataService, repositoryId, userId, "read the code health trend for");
            if (error != null) return error;

            var trend = await trendHandler.Handle(new GetCodeHealthTrendQuery(repositoryId));

            return trend is null
                ? Results.NotFound(new ErrorResponse
                {
                    Title = "Nothing to chart",
                    Detail = "This repository has no analysed commits yet. Run an analysis and try again.",
                    Code = "no_analysed_commits",
                    Status = (int)HttpStatusCode.NotFound
                })
                : Results.Ok(trend);
        })
        .WithName("GetCodeHealthTrend")
        .WithSummary("Monthly code health for the trailing two years, measured at the last commit on or before each 1st");
    }
}
