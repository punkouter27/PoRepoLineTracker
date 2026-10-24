using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;

namespace PoRepoLineTracker.API.Storage;

public class RepositoryDataService : IRepositoryDataService
{
    private readonly TableClient _repositoryTableClient;
    private readonly TableClient _commitLineCountTableClient;
    private readonly ILogger<RepositoryDataService> _logger;

    // SemaphoreSlim(1,1) prevents concurrent background tasks from double-initializing tables
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _tablesInitialized = false;

    public RepositoryDataService(TableServiceClient tableServiceClient, IConfiguration configuration, ILogger<RepositoryDataService> logger)
    {
        _logger = logger;
        var repositoryTableName = configuration[ConfigKeys.AzureTableStorage.RepositoryTableName] ?? "PoRepoLineTrackerRepositories";
        var commitLineCountTableName = configuration[ConfigKeys.AzureTableStorage.CommitLineCountTableName] ?? "PoRepoLineTrackerCommitLineCounts";

        _repositoryTableClient = tableServiceClient.GetTableClient(repositoryTableName);
        _commitLineCountTableClient = tableServiceClient.GetTableClient(commitLineCountTableName);
    }

    private async Task EnsureTablesExistAsync()
    {
        if (_tablesInitialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_tablesInitialized) return; // double-check after acquiring lock
            await _repositoryTableClient.CreateIfNotExistsAsync();
            await _commitLineCountTableClient.CreateIfNotExistsAsync();
            _tablesInitialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring tables exist: {ErrorMessage}", ex.Message);
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task AddRepositoryAsync(GitHubRepository repository)
    {
        await EnsureTablesExistAsync();
        _logger.LogInformation("Adding repository {RepoName} to Table Storage.", repository.Name);
        try
        {
            var entity = GitHubRepositoryEntity.FromDomainModel(repository);
            _logger.LogInformation("About to call AddEntityAsync for {RepoName}", repository.Name);
            await _repositoryTableClient.AddEntityAsync(entity);
            _logger.LogInformation("AddEntityAsync completed for {RepoName}", repository.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FATAL ERROR in AddEntityAsync for {RepoName}: {Message}. Type: {ExceptionType}. Stack: {StackTrace}",
                repository.Name, ex.Message, ex.GetType().FullName, ex.StackTrace);
            throw; // Re-throw to propagate to caller
        }
        _logger.LogInformation("Repository {RepoName} added successfully.", repository.Name);
    }

    public async Task UpdateRepositoryAsync(GitHubRepository repository)
    {
        await EnsureTablesExistAsync();
        _logger.LogInformation("Updating repository {RepoName} in Table Storage.", repository.Name);

        // ETag.All: last write wins. This used to read the row first "for optimistic
        // concurrency" and pass back the ETag it had just fetched — a second round-trip that
        // protected nothing, since the caller's copy was never compared. UpdateEntity (not
        // Upsert) so updating a repository deleted mid-analysis still fails with 404.
        var entityToUpdate = GitHubRepositoryEntity.FromDomainModel(repository);
        await _repositoryTableClient.UpdateEntityAsync(entityToUpdate, ETag.All, TableUpdateMode.Replace);
        _logger.LogInformation("Repository {RepoName} updated successfully.", repository.Name);
    }

    public async Task<GitHubRepository?> GetRepositoryByIdAsync(RepositoryId id)
    {
        await EnsureTablesExistAsync();
        // ponytail: filters on a non-key property, so this is a table scan. Fine at portfolio
        // size (hundreds of rows); if it grows, take the UserId and query by PartitionKey.
        // Debug, not Information: every ownership check calls this, and two lines per request
        // were a measurable share of log ingestion.
        _logger.LogDebug("Getting repository by Id: {RepositoryId} from Table Storage.", id);
        await foreach (var entity in _repositoryTableClient.QueryAsync<GitHubRepositoryEntity>(e => e.Id == id.Value))
        {
            return entity.ToDomainModel();
        }
        _logger.LogWarning("Repository with Id {RepositoryId} not found.", id);
        return null;
    }

    public async Task<IEnumerable<GitHubRepository>> GetAllRepositoriesAsync(UserId userId)
    {
        await EnsureTablesExistAsync();
        _logger.LogInformation("Getting all repositories for user {UserId} from Table Storage.", userId);
        var repositories = (await QueryUserEntitiesAsync(userId)).Select(e => e.ToDomainModel()).ToList();
        _logger.LogInformation("Found {Count} repositories for user {UserId}.", repositories.Count, userId);
        return repositories;
    }

    /// <summary>The one "every repository row of this user" query, shared by read and remove-all.</summary>
    private async Task<List<GitHubRepositoryEntity>> QueryUserEntitiesAsync(UserId userId)
    {
        var entities = new List<GitHubRepositoryEntity>();
        await foreach (var entity in _repositoryTableClient.QueryAsync<GitHubRepositoryEntity>(e => e.PartitionKey == userId.ToString()))
            entities.Add(entity);
        return entities;
    }

    public async Task<IEnumerable<GitHubRepository>> GetResumableRepositoriesAsync()
    {
        await EnsureTablesExistAsync();
        var repositories = new List<GitHubRepository>();
        // ponytail: cross-partition scan filtered in memory — the repository table is
        // portfolio-sized (hundreds of rows). If it ever outgrows that, push an OData filter
        // (LastAnalyzedCommitDate eq null) into QueryAsync instead.
        await foreach (var entity in _repositoryTableClient.QueryAsync<GitHubRepositoryEntity>())
        {
            // Never attempted, not merely "no commits yet": a repository whose analysis ran and
            // failed (or found an empty repo) has an attempt stamp and is the user's to retry.
            // Or queued and never finished: the stamp outlives the in-memory queue entry.
            if ((entity.LastAnalyzedCommitDate is null && entity.LastAnalysisAttemptUtc is null)
                || entity.AnalysisQueuedUtc is not null)
                repositories.Add(entity.ToDomainModel());
        }
        _logger.LogInformation("Found {Count} repositories with unfinished analysis across all users.", repositories.Count);
        return repositories;
    }

    public async Task<IReadOnlyList<(GitHubRepository Repository, IReadOnlyList<CommitLineCount> Commits)>> GetAllRepositoriesWithCommitsAsync(UserId userId)
    {
        var repositories = (await GetAllRepositoriesAsync(userId)).ToList();

        // One round-trip per repository, parallelised. A 50-repo portfolio pays 50 serial
        // round-trips otherwise, which is what makes these pages slow. Commits are ordered by
        // date ascending here so every caller can index them with [^1] for the newest and
        // sliding-window search by index rather than re-sorting.
        var pairs = await Task.WhenAll(repositories.Select(async repo => (
            repo,
            commits: (IReadOnlyList<CommitLineCount>)(await GetCommitLineCountsByRepositoryIdAsync(repo.Id))
                .OrderBy(c => c.CommitDate)
                .ToList())));

        return pairs
            .Select(p => (Repository: p.repo, Commits: p.commits))
            .ToList();
    }

    public async Task AddCommitLineCountAsync(CommitLineCount commitLineCount)
    {
        await EnsureTablesExistAsync();
        var entity = CommitLineCountEntity.FromDomainModel(commitLineCount);
        // Use UpsertEntityAsync to add or replace the entity
        // No per-commit log lines here: two Information entries per commit, per analysis, was
        // thousands of lines for one repository. The handler logs each processed commit once.
        await _commitLineCountTableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
    }

    public async Task<IEnumerable<CommitLineCount>> GetCommitLineCountsByRepositoryIdAsync(RepositoryId repositoryId)
    {
        await EnsureTablesExistAsync();
        _logger.LogInformation("Getting commit line counts for repository {RepositoryId} from Table Storage.", repositoryId);
        var commitLineCounts = new List<CommitLineCount>();
        await foreach (var entity in _commitLineCountTableClient.QueryAsync<CommitLineCountEntity>(e => e.PartitionKey == repositoryId.ToString()))
        {
            commitLineCounts.Add(entity.ToDomainModel());
        }
        _logger.LogInformation("Found {Count} commit line counts for repository {RepositoryId}.", commitLineCounts.Count, repositoryId);
        return commitLineCounts;
    }

    // CommitExistsAsync removed. It was a per-SHA point read used from inside the analysis loop,
    // so a repository with N commits cost N round-trips (and 2N Information-level log lines) to
    // answer a question that GetCommitLineCountsByRepositoryIdAsync already answers for the whole
    // repository in one query. The analysis handler pre-loads that set and looks SHAs up in it;
    // nothing else ever called this.

    public async Task CheckConnectionAsync()
    {
        await EnsureTablesExistAsync();
        _logger.LogInformation("Checking Azure Table Storage connection.");
        // Attempt a simple query to check connectivity
        await foreach (var _ in _repositoryTableClient.QueryAsync<TableEntity>(maxPerPage: 1))
        {
            // Just iterate once to ensure connection is established
            break;
        }
        _logger.LogInformation("Azure Table Storage connection successful.");
    }

    public async Task DeleteCommitLineCountsForRepositoryAsync(RepositoryId repositoryId)
    {
        await EnsureTablesExistAsync();
        _logger.LogInformation("Deleting all commit line counts for repository {RepositoryId} from Table Storage.", repositoryId);
        var entitiesToDelete = new List<CommitLineCountEntity>();
        await foreach (var entity in _commitLineCountTableClient.QueryAsync<CommitLineCountEntity>(e => e.PartitionKey == repositoryId.ToString()))
        {
            entitiesToDelete.Add(entity);
        }

        if (entitiesToDelete.Any())
        {
            // 100-row transactions (the service's limit; all rows share the repository's
            // partition), sequentially. This was one unbatched DeleteEntityAsync per commit, all
            // started at once — thousands of concurrent requests for a large repository.
            foreach (var batch in entitiesToDelete.Chunk(100))
            {
                await _commitLineCountTableClient.SubmitTransactionAsync(
                    batch.Select(entity => new TableTransactionAction(TableTransactionActionType.Delete, entity, ETag.All)));
            }
            _logger.LogInformation("Deleted {Count} commit line counts for repository {RepositoryId}.", entitiesToDelete.Count, repositoryId);
        }
        else
        {
            _logger.LogInformation("No commit line counts found to delete for repository {RepositoryId}.", repositoryId);
        }
    }

    public async Task DeleteRepositoryAsync(RepositoryId repositoryId)
    {
        await EnsureTablesExistAsync();
        _logger.LogInformation("Deleting repository {RepositoryId} and all associated data from Table Storage.", repositoryId);

        // First, delete all commit line counts for this repository
        await DeleteCommitLineCountsForRepositoryAsync(repositoryId);

        // Then, find and delete the repository entity
        try
        {
            await foreach (var entity in _repositoryTableClient.QueryAsync<GitHubRepositoryEntity>(r => r.Id == repositoryId.Value))
            {
                await _repositoryTableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, ETag.All);
                _logger.LogInformation("Repository entity deleted successfully for repository {RepositoryId}.", repositoryId);
                break; // Should only be one match
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting repository {RepositoryId}. Error: {ErrorMessage}", repositoryId, ex.Message);
            throw;
        }
    }

    public async Task<GitHubRepository?> GetRepositoryByOwnerAndNameAsync(string owner, string name, UserId userId)
    {
        await EnsureTablesExistAsync();
        _logger.LogInformation("Getting repository by Owner: {Owner} and Name: {Name} for user {UserId} from Table Storage.", owner, name, userId);
        try
        {
            var rowKey = $"{owner}_{name}";
            var entity = await _repositoryTableClient.GetEntityAsync<GitHubRepositoryEntity>(userId.ToString(), rowKey);
            _logger.LogInformation("Found repository {RepoName} by Owner {Owner} and Name {Name} for user {UserId}.", name, owner, name, userId);
            return entity.Value.ToDomainModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogInformation("Repository with Owner {Owner} and Name {Name} not found for user {UserId}.", owner, name, userId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting repository by Owner {Owner} and Name {Name} for user {UserId}. Error: {ErrorMessage}", owner, name, userId, ex.Message);
            throw;
        }
    }

    public async Task<IEnumerable<DailyLineCountDto>> GetLineCountHistoryAsync(RepositoryId repositoryId, int days)
    {
        _logger.LogInformation("Getting line count history for repository {RepositoryId} for the last {Days} days from Table Storage.", repositoryId, days);

        // Get all commit line counts for the repository
        var commitLineCounts = await GetCommitLineCountsByRepositoryIdAsync(repositoryId);

        // Filter commits within the specified date range
        var cutoffDate = DateTime.UtcNow.AddDays(-days);
        var filteredCommits = commitLineCounts
            .Where(c => c.CommitDate >= cutoffDate)
            .OrderBy(c => c.CommitDate)
            .ToList();

        _logger.LogInformation("Found {Count} commits within the last {Days} days for repository {RepositoryId}.", filteredCommits.Count, days, repositoryId);

        // Group commits by date and aggregate the data.
        // TotalLines is a per-commit snapshot of the entire repo at that point, so use the
        // last commit of each day (highest CommitDate) rather than summing all snapshots.
        var dailyData = filteredCommits
            .GroupBy(c => c.CommitDate.Date)
            .Select(g => new DailyLineCountDto
            {
                Date = g.Key,
                TotalLines = g.OrderByDescending(c => c.CommitDate).First().TotalLines,
                TotalLinesAdded = g.Sum(c => c.LinesAdded),
                TotalLinesDeleted = g.Sum(c => c.LinesRemoved),
                TotalLinesChanged = g.Sum(c => c.LinesAdded + c.LinesRemoved),
                CommitCount = g.Count(),
                LinesByFileType = g
                    .SelectMany(c => c.LinesByFileType)
                    .GroupBy(kvp => kvp.Key)
                    .ToDictionary(fileGroup => fileGroup.Key, fileGroup => fileGroup.Sum(kvp => kvp.Value))
            })
            .OrderBy(d => d.Date)
            .ToList();

        _logger.LogInformation("Aggregated data into {Count} daily entries for repository {RepositoryId}.", dailyData.Count, repositoryId);
        return dailyData;
    }

    /// <summary>
    /// Removes all repository rows of a user and their commit rows, from ONE read of the user's
    /// partition — it used to be read three times (the handler, here, and the entity purge).
    /// </summary>
    public async Task<IReadOnlyList<GitHubRepository>> RemoveAllRepositoriesAsync(UserId userId)
    {
        await EnsureTablesExistAsync();
        _logger.LogInformation("Starting removal of all repositories and commit data for user {UserId} from Azure Table Storage.", userId);

        try
        {
            var entities = await QueryUserEntitiesAsync(userId);
            var repositories = entities.Select(e => e.ToDomainModel()).ToList();

            foreach (var repo in repositories)
            {
                await DeleteCommitLineCountsForRepositoryAsync(repo.Id);
            }

            await RemoveRepositoryEntitiesAsync(entities, userId);

            _logger.LogInformation("Successfully removed all repositories and commit data for user {UserId} from Azure Table Storage.", userId);
            return repositories;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while removing all data for user {UserId} from Azure Table Storage: {ErrorMessage}", userId, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Strategy Pattern: Implements batch deletion strategy for repository entities for a specific user.
    /// </summary>
    private async Task RemoveRepositoryEntitiesAsync(List<GitHubRepositoryEntity> entitiesToDelete, UserId userId)
    {
        if (entitiesToDelete.Any())
        {
            _logger.LogInformation("Found {Count} repository entities to delete for user {UserId}.", entitiesToDelete.Count, userId);

            // Delete entities in parallel batches for better performance
            const int batchSize = 100;
            var batches = entitiesToDelete
                .Select((entity, index) => new { entity, index })
                .GroupBy(x => x.index / batchSize)
                .Select(g => g.Select(x => x.entity).ToList());

            foreach (var batch in batches)
            {
                // ETag.All, not entity.ETag: this is an unconditional purge, so a write that
                // lands between the partition read and this delete must not abort it. Passing
                // the stale ETag made any concurrent update fail the whole call with a 412
                // ("The update condition specified in the request was not satisfied") — surfacing
                // as an intermittent 500 from DELETE /api/repositories/all.
                var deleteTasks = batch.Select(entity =>
                    _repositoryTableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, ETag.All));

                await Task.WhenAll(deleteTasks);
                _logger.LogDebug("Deleted batch of {Count} repository entities.", batch.Count);
            }

            _logger.LogInformation("Successfully deleted {Count} repository entities for user {UserId}.", entitiesToDelete.Count, userId);
        }
        else
        {
            _logger.LogInformation("No repository entities found to delete for user {UserId}.", userId);
        }
    }

}
