namespace PoRepoLineTracker.Api.Features.Repositories;

public record DeleteRepositoryCommand(RepositoryId RepositoryId);

public class DeleteRepositoryCommandHandler(
    IRepositoryDataService repositoryDataService,
    IGitHubService gitHubService,
    ICodeHealthSnapshotStore snapshotStore,
    RepositoryWebhooks webhooks,
    ILogger<DeleteRepositoryCommandHandler> logger)
{
    public async Task Handle(DeleteRepositoryCommand request, CancellationToken cancellationToken = default)
    {
        // Read first: the hook's id lives on the row. A hook left behind would keep posting
        // every push to a route that now answers 404.
        if (await repositoryDataService.GetRepositoryByIdAsync(request.RepositoryId) is { } repository)
            await webhooks.RemoveAsync(repository);

        await repositoryDataService.DeleteRepositoryAsync(request.RepositoryId);
        await DeleteDerivedDataAsync(request.RepositoryId, gitHubService, snapshotStore, logger);
    }

    /// <summary>
    /// Removes what a repository leaves behind outside its own table rows: the clone on disk and
    /// the code-health snapshots.
    ///
    /// <para>Delete used to remove table rows only, so a user who stopped tracking a private
    /// repository left its full source on the server's persistent share indefinitely. "Remove
    /// all" went the other way and recursively deleted the SHARED clone root — every user's
    /// clones, including ones mid-analysis. Both now call this, per repository.</para>
    ///
    /// <para>Best-effort and step-by-step: the rows are already gone, so a locked pack file must
    /// not turn a successful delete into an error the user cannot retry.</para>
    /// </summary>
    internal static async Task DeleteDerivedDataAsync(
        RepositoryId repositoryId,
        IGitHubService gitHubService,
        ICodeHealthSnapshotStore snapshotStore,
        ILogger logger)
    {
        try
        {
            await snapshotStore.DeleteForRepositoryAsync(repositoryId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete code-health snapshots for repository {RepositoryId}", repositoryId);
        }

        try
        {
            // The clone directory is always derived from the id, never read from the (deleted) row.
            await gitHubService.DeleteLocalRepositoryAsync(GitHubRepository.CloneDirectoryName(repositoryId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete the local clone of repository {RepositoryId}", repositoryId);
        }
    }
}
