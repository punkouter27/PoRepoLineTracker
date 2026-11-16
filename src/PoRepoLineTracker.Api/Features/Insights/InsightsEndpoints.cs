namespace PoRepoLineTracker.Api.Features.Insights;

internal static class InsightsEndpoints
{
    internal static void MapInsightsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var insights = endpoints.MapGroup("/api/insights")
            .WithTags("Insights")
            .RequireAuthorization();

        // No IDOR check of the kind the repository routes carry: the query is scoped by the
        // caller's own user id rather than by a supplied repository id, so there is no identifier
        // here for a caller to substitute. The same holds for the year route below.
        insights.MapGet("/portfolio", async (HttpContext ctx, GetPortfolioInsightsQueryHandler portfolioHandler) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var result = await portfolioHandler.Handle(new GetPortfolioInsightsQuery(userId));
            return Results.Ok(result);
        })
        .WithName("GetPortfolioInsights");

        // Year in Review. Scoped by the caller's id like everything else here. The year is
        // bounded so the date arithmetic cannot overflow and nobody asks for year 9999.
        insights.MapGet("/year/{year:int}", async (int year, int? offset, HttpContext ctx, GetYearInReviewQueryHandler yearHandler, CancellationToken cancellationToken) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();
            if (year < 1970 || year > DateTime.UtcNow.Year + 1)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["year"] = ["Pick a year between 1970 and next year."] });

            return Results.Ok(await yearHandler.Handle(new GetYearInReviewQuery(userId, year, offset ?? 0), cancellationToken));
        })
        .WithName("GetYearInReview");
    }
}
