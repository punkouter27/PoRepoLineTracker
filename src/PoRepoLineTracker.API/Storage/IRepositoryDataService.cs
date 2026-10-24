
namespace PoRepoLineTracker.API.Storage;

public interface IRepositoryDataService
{
    Task AddRepositoryAsync(GitHubRepository repository);
    Task UpdateRepositoryAsync(GitHubRepository repository);
    Task<GitHubRepository?> GetRepositoryByIdAsync(RepositoryId id);
    Task<GitHubRepository?> GetRepositoryByOwnerAndNameAsync(string owner, string name, UserId userId);
    Task<IEnumerable<GitHubRepository>> GetAllRepositoriesAsync(UserId userId);

    /// <summary>
    /// Every repository across all users whose analysis has not run to a conclusion: never
    /// attempted (no analysed commit and no recorded attempt), or queued and not finished
    /// (<see cref="GitHubRepository.AnalysisQueuedUtc"/> set). Backs the startup sweep that
    /// resumes analyses a restart interrupted.
    /// </summary>
    Task<IEnumerable<GitHubRepository>> GetResumableRepositoriesAsync();

    Task AddCommitLineCountAsync(CommitLineCount commitLineCount);
    Task<IEnumerable<CommitLineCount>> GetCommitLineCountsByRepositoryIdAsync(RepositoryId repositoryId);
    Task<IEnumerable<DailyLineCountDto>> GetLineCountHistoryAsync(RepositoryId repositoryId, int days); // Added for line count history

    /// <summary>
    /// Every repository the user owns, paired with its commits, fetched in parallel.
    ///
    /// <para><b>Why this lives on the data service.</b> Three handlers — portfolio insights,
    /// weekly digest, year-in-code — were each opening a Task.WhenAll over the same
    /// per-repository round-trip and writing it slightly differently (one ordered, one not, one
    /// filtered). Pinning the fan-out here makes a fourth handler free, and the order — commits
    /// ordered by date ascending — is what the chart code now assumes.</para>
    /// </summary>
    Task<IReadOnlyList<(GitHubRepository Repository, IReadOnlyList<CommitLineCount> Commits)>> GetAllRepositoriesWithCommitsAsync(UserId userId);

    // No CommitExistsAsync. A per-SHA existence check invites being called from inside a loop over
    // commits, which is exactly what happened: N round-trips to answer what the one call above
    // returns for the whole repository. Callers pre-load and look up in memory.

    Task DeleteCommitLineCountsForRepositoryAsync(RepositoryId repositoryId); // Added for temporary endpoint
    Task DeleteRepositoryAsync(RepositoryId repositoryId);
    /// <summary>
    /// Removes every repository a user tracks (and their commit rows) and returns what was
    /// removed — once the rows are gone, nothing else records which clones were this user's.
    /// </summary>
    Task<IReadOnlyList<GitHubRepository>> RemoveAllRepositoriesAsync(UserId userId);
    Task CheckConnectionAsync();
}
