using Microsoft.AspNetCore.Mvc;
using System.Net;
using Serilog;

namespace PoRepoLineTracker.API.Features.Repositories;

internal static class RepositoryEndpoints
{
    /// <summary>
    /// Most repositories one bulk add may carry. Each new one queues a clone and a full history
    /// walk; without a cap a single request could queue thousands.
    /// </summary>
    private const int MaxBulkRepositories = 200;

    /// <summary>409 body for a re-analyse or delete that arrives while an analysis is queued or running.</summary>
    internal static IResult AnalysisInProgress() => Results.Conflict(new ErrorResponse
    {
        Title = "Analysis in progress",
        Detail = "This repository is being analysed. Try again when it finishes.",
        Code = "analysis_in_progress",
        Status = (int)HttpStatusCode.Conflict
    });

    internal static void MapRepositoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // One group carries the prefix and the authorization requirement for the whole
        // slice. Every route below is authenticated because the group says so, not because each
        // endpoint remembered to say so (the "#6 fix" comments below record the era when they didn't).
        var repos = endpoints.MapGroup("/api/repositories")
            .WithTags("Repositories")
            .RequireAuthorization();

        // There is no POST "/" for a single repository. /bulk is the one write path, and takes a
        // one-element array for the single case. The two were not merely redundant, they behaved
        // differently on the case that matters: /bulk checks GetRepositoryByOwnerAndNameAsync and
        // buckets a repeat into AlreadyTracked, while the single-add handler inserted
        // unconditionally — so adding the same repository twice through it produced two rows for
        // one GitHub repo. /bulk also validates through the shared repository rules in .Shared and
        // queues analysis for what it actually added; neither happened here.

        repos.MapGet("/", async (HttpContext ctx, GetAllRepositoriesQueryHandler getAllHandler) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var repositories = await getAllHandler.Handle(new GetAllRepositoriesQuery(userId));
            return Results.Ok(repositories);
        })
        .WithName("GetAllRepositories");

        // #6 fix: added RequireAuthorization() - was unprotected
        repos.MapGet("/{repositoryId}/linehistory/{days}", async (RepositoryId repositoryId, int days, HttpContext ctx, GetLineCountHistoryQueryHandler lineHistoryHandler, IRepositoryDataService repoDataService) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var (_, error) = await RepositoryOwnership.AuthorizeAsync(repoDataService, repositoryId, userId, "read linehistory for");
            if (error != null) return error;

            var lineHistory = await lineHistoryHandler.Handle(new GetLineCountHistoryQuery(repositoryId, days));
            return Results.Ok(lineHistory);
        })
        .WithName("GetRepositoryLineHistory");

        repos.MapGet("/{repositoryId}/punchcard/{days}", async (RepositoryId repositoryId, int days, HttpContext ctx, GetRepositoryPunchcardQueryHandler punchcardHandler, IRepositoryDataService repoDataService) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var (_, error) = await RepositoryOwnership.AuthorizeAsync(repoDataService, repositoryId, userId, "read punchcard for");
            if (error != null) return error;

            var punchcard = await punchcardHandler.Handle(new GetRepositoryPunchcardQuery(repositoryId, days));
            return Results.Ok(punchcard);
        })
        .WithName("GetRepositoryPunchcard");

        repos.MapGet("/{repositoryId}/hotspots/{days}", async (RepositoryId repositoryId, int days, HttpContext ctx, GetRepositoryHotspotsQueryHandler hotspotsHandler, IRepositoryDataService repoDataService, CancellationToken cancellationToken) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var (_, error) = await RepositoryOwnership.AuthorizeAsync(repoDataService, repositoryId, userId, "read hotspots for");
            if (error != null) return error;

            // A git log over decades of history is the expensive end of this; ten years covers
            // every range the page offers.
            var hotspots = await hotspotsHandler.Handle(new GetRepositoryHotspotsQuery(repositoryId, Math.Clamp(days, 1, 3650)), cancellationToken);
            return hotspots is null ? Results.NotFound() : Results.Ok(hotspots);
        })
        .WithName("GetRepositoryHotspots");

        repos.MapGet("/allcharts/{days}", async (int days, HttpContext ctx, GetAllRepositoriesLineCountHistoryQueryHandler allChartsHandler) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var data = await allChartsHandler.Handle(new GetAllRepositoriesLineCountHistoryQuery(days, userId));
            return Results.Ok(data);
        })
        .WithName("GetAllRepositoriesLineHistory");

        // #6 fix: added RequireAuthorization() + ownership check - was fully unprotected
        repos.MapDelete("/{repositoryId}", async (RepositoryId repositoryId, HttpContext ctx, DeleteRepositoryCommandHandler deleteHandler, IRepositoryDataService repoDataService, AnalysisQueue queue) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            // Ownership guard: only the owning user may delete their repository
            var (_, error) = await RepositoryOwnership.AuthorizeAsync(repoDataService, repositoryId, userId, "delete");
            if (error != null) return error;

            if (queue.IsPending(repositoryId))
                return AnalysisInProgress();

            try
            {
                await deleteHandler.Handle(new DeleteRepositoryCommand(repositoryId));
                Log.Information("Repository {RepositoryId} deleted successfully via API.", repositoryId);
                return Results.NoContent();
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound($"Repository with ID {repositoryId} not found.");
            }
        })
        .WithName("DeleteRepository");

        repos.MapDelete("/all", async (HttpContext ctx, RemoveAllRepositoriesCommandHandler removeAllHandler, IRepositoryDataService repoDataService, AnalysisQueue queue) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
            {
                Log.Warning("Remove all repositories failed: No valid UserId claim found");
                return Results.Unauthorized();
            }

            // Same 409 as single delete: a running job would keep upserting commit rows for
            // repositories this call just removed, and a queued one would clone them again.
            if ((await repoDataService.GetAllRepositoriesAsync(userId)).Any(r => queue.IsPending(r.Id)))
                return AnalysisInProgress();

            Log.Information("Starting removal of all repositories for user {UserId}", userId);
            await removeAllHandler.Handle(new RemoveAllRepositoriesCommand(userId));
            Log.Information("All repositories for user {UserId} removed successfully via API.", userId);
            return Results.NoContent();
        })
        .WithName("RemoveAllRepositories");

        repos.MapPost("/bulk", async ([FromBody] IEnumerable<BulkRepositoryDto> repositories, HttpContext ctx, AddMultipleRepositoriesCommandHandler addHandler, AnalysisQueue queue) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var repoList = repositories?.ToList() ?? [];
            if (repoList.Count > MaxBulkRepositories)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["repositories"] = [$"At most {MaxBulkRepositories} repositories can be added per request."]
                });
            }

            // Validate each entry with the shared repository rules.
            foreach (var dto in repoList)
            {
                if (RepositoryValidators.Validate(dto) is { } errors)
                    return Results.ValidationProblem(errors);
            }

            var result = await addHandler.Handle(new AddMultipleRepositoriesCommand(
                repoList, userId));

            Log.Information("Bulk add: Added={Added}, AlreadyTracked={AlreadyTracked}",
                result.Added.Count, result.AlreadyTracked.Count);

            // Queue analysis only for truly NEW repositories — already-tracked repos are already
            // analysed. No queued stamp: a row with no attempt is already what the startup sweep
            // resumes, so a restart before these run loses nothing.
            foreach (var added in result.Added)
                queue.Enqueue(added.Id);

            return Results.Ok(result);
        })
        .RequireRateLimiting(RateLimitPolicies.Analysis)
        .WithName("AddMultipleRepositories");

        repos.MapPost("/{repositoryId}/reanalyze", async (RepositoryId repositoryId, HttpContext ctx, AnalysisQueue queue, IRepositoryDataService repoDataService) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var (repository, error) = await RepositoryOwnership.AuthorizeAsync(repoDataService, repositoryId, userId, "reanalyze");
            if (error != null) return error;

            // The handler would skip silently (it holds a per-repository lock) while this
            // endpoint answered 202 "started". The queue's own check also covers "queued, not
            // yet running", which the lock cannot see.
            if (!await queue.EnqueueReanalysisAsync(repository!, repoDataService))
                return AnalysisInProgress();

            Log.Information("Re-analysis queued for repository {RepositoryId} by user {UserId}", repositoryId, userId);
            return Results.Accepted(value: new { message = "Re-analysis started. All commit data will be re-calculated with your current file extension preferences." });
        })
        .RequireRateLimiting(RateLimitPolicies.Analysis)
        .WithName("ReanalyzeRepository");

        // The owner's opt-in to the anonymous badge and share page (PublicBadgeEndpoints).
        repos.MapPut("/{repositoryId}/visibility", async (RepositoryId repositoryId, [FromBody] RepositoryVisibilityDto body, HttpContext ctx, IRepositoryDataService repoDataService, AnalysisQueue queue, Microsoft.Extensions.Caching.Hybrid.HybridCache cache) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var (repository, error) = await RepositoryOwnership.AuthorizeAsync(repoDataService, repositoryId, userId, "change visibility of");
            if (error != null) return error;

            // A running analysis writes back the row it loaded when it started, which would
            // quietly undo this change at its end.
            if (queue.IsPending(repositoryId))
                return AnalysisInProgress();

            repository!.IsPublic = body.IsPublic;
            await repoDataService.UpdateRepositoryAsync(repository);
            await PublicBadgeEndpoints.EvictAsync(cache, repositoryId);
            Log.Information("Repository {RepositoryId} made {Visibility} by user {UserId}", repositoryId, body.IsPublic ? "public" : "private", userId);
            return Results.Ok(new RepositoryVisibilityDto { IsPublic = repository.IsPublic });
        })
        .WithName("SetRepositoryVisibility");

        // #6 fix: added RequireAuthorization() - was unprotected
        repos.MapGet("/{repositoryId}/file-extension-percentages", async (RepositoryId repositoryId, HttpContext ctx, GetFileExtensionPercentagesQueryHandler extensionHandler, IRepositoryDataService repoDataService) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var (_, error) = await RepositoryOwnership.AuthorizeAsync(repoDataService, repositoryId, userId, "read file-extension-percentages for");
            if (error != null) return error;

            var percentages = await extensionHandler.Handle(new GetFileExtensionPercentagesQuery(repositoryId));
            return Results.Ok(percentages);
        })
        .WithName("GetFileExtensionPercentages");

        // Analysis progress endpoint — returns live step/commit progress for an active analysis job.
        // Ownership check: only the owning user may read progress for their repo.
        repos.MapGet("/{repositoryId}/analysis-progress", async (RepositoryId repositoryId, HttpContext ctx, IRepositoryDataService repoDataService, IAnalysisProgressService progressService) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var (_, error) = await RepositoryOwnership.AuthorizeAsync(repoDataService, repositoryId, userId, "read analysis-progress for");
            if (error != null) return error;

            var progress = progressService.GetProgress(repositoryId);
            if (progress == null)
                return Results.NotFound(new { message = "No active or recent analysis job found for this repository." });

            return Results.Ok(progress);
        })
        .WithName("GetRepositoryAnalysisProgress");

    }
}
