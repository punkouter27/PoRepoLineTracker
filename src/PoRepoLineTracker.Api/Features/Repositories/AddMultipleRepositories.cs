using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using PoRepoLineTracker.Api.Telemetry;
using System.Diagnostics;

namespace PoRepoLineTracker.Api.Features.Repositories;

/// <summary>
/// The single write path for tracking repositories. A one-element list is the "add one" case —
/// there is no separate AddRepositoryCommand, and deliberately so: the one that used to exist
/// inserted without checking for an existing row, so adding the same repository twice through it
/// produced two records for one GitHub repo.
/// </summary>
public record AddMultipleRepositoriesCommand(IEnumerable<BulkRepositoryDto> Repositories, UserId UserId);

public class AddMultipleRepositoriesCommandHandler
{
    private readonly IRepositoryDataService _repositoryDataService;
    private readonly ILogger<AddMultipleRepositoriesCommandHandler> _logger;

    public AddMultipleRepositoriesCommandHandler(
        IRepositoryDataService repositoryDataService,
        ILogger<AddMultipleRepositoriesCommandHandler> logger)
    {
        _repositoryDataService = repositoryDataService;
        _logger = logger;
    }

    public async Task<BulkAddResult> Handle(AddMultipleRepositoriesCommand request, CancellationToken cancellationToken = default)
    {
        // Span and metrics carried over from the deleted single-add handler, which was the only
        // add path that reported any. Now that this is the only one, it is where they belong.
        using var activity = AppTelemetry.ActivitySource.StartActivity("AddRepositories");
        var stopwatch = Stopwatch.StartNew();

        var repoList = request.Repositories.ToList();

        var added = new List<GitHubRepository>();
        var alreadyTracked = new List<GitHubRepository>();

        // Insert each new repository; a repeat goes to AlreadyTracked.
        foreach (var repo in request.Repositories)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(repo.Owner))
                {
                    _logger.LogWarning("Skipping repository with empty Owner. RepoName={RepoName}", repo.RepoName ?? "NULL");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(repo.RepoName))
                {
                    _logger.LogWarning("Skipping repository with empty RepoName. Owner={Owner}", repo.Owner ?? "NULL");
                    continue;
                }

                var existingRepo = await _repositoryDataService.GetRepositoryByOwnerAndNameAsync(repo.Owner, repo.RepoName, request.UserId);
                if (existingRepo != null)
                {
                    _logger.LogInformation("Repository {Owner}/{Name} already tracked with ID {Id} — returning in AlreadyTracked bucket.",
                        repo.Owner, repo.RepoName, existingRepo.Id);
                    alreadyTracked.Add(existingRepo);
                    continue;
                }

                var newRepo = new GitHubRepository
                {
                    Id = RepositoryId.New(),
                    UserId = request.UserId,
                    Owner = repo.Owner,
                    Name = repo.RepoName,
                    // Derived, never taken from the request — see BulkRepositoryDto.
                    CloneUrl = GitHubRepository.CloneUrlFor(repo.Owner, repo.RepoName),
                    LastAnalyzedCommitDate = null // null until first analysis completes
                };

                await _repositoryDataService.AddRepositoryAsync(newRepo);
                added.Add(newRepo);

                AppTelemetry.RepositoriesAdded.Add(1,
                    new KeyValuePair<string, object?>("status", "success"),
                    new KeyValuePair<string, object?>("owner", repo.Owner));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EXCEPTION while adding repository {Owner}/{Name}: {Message}",
                    repo.Owner ?? "NULL", repo.RepoName ?? "NULL", ex.Message);

                AppTelemetry.RepositoriesAdded.Add(1,
                    new KeyValuePair<string, object?>("status", "failure"),
                    new KeyValuePair<string, object?>("owner", repo.Owner ?? "unknown"),
                    new KeyValuePair<string, object?>("error.type", ex.GetType().Name));

                // Continue with other repositories even if one fails
            }
        }

        stopwatch.Stop();
        AppTelemetry.AddRepositoryDuration.Record(stopwatch.ElapsedMilliseconds,
            new KeyValuePair<string, object?>("count", repoList.Count));

        activity?.SetTag("repositories.requested", repoList.Count);
        activity?.SetTag("repositories.added", added.Count);
        activity?.SetTag("repositories.already_tracked", alreadyTracked.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        _logger.LogInformation("Bulk add for user {UserId}: Added={Added}, AlreadyTracked={AlreadyTracked} in {ElapsedMs}ms", request.UserId,
            added.Count, alreadyTracked.Count, stopwatch.ElapsedMilliseconds);

        return new BulkAddResult
        {
            Added = added,
            AlreadyTracked = alreadyTracked
        };
    }
}
