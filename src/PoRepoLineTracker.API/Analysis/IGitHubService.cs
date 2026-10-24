
namespace PoRepoLineTracker.API.Analysis;

public interface IGitHubService
{
    /// <summary>
    /// Turns a stored <c>LocalPath</c> (relative to the clone base directory) into the absolute
    /// path the read operations below take. The ONE place a repository's on-disk location is
    /// decided: callers resolve once and pass an absolute path.
    /// </summary>
    string ResolveRepositoryPath(string localPath);

    Task<string> CloneRepositoryAsync(string repoUrl, string localPath, string? accessToken = null);
    Task<string> PullRepositoryAsync(string localPath, string? accessToken = null);

    /// <summary>
    /// Deletes the local repository directory — before a re-clone, or because the repository is
    /// no longer tracked.
    /// </summary>
    Task DeleteLocalRepositoryAsync(string localPath);

    // ── Reads. All take an ABSOLUTE repository path — see ResolveRepositoryPath. ──────────────

    /// <summary>Whether an absolute path holds a valid git repository.</summary>
    Task<bool> IsRepositoryValidAsync(string repositoryPath);

    /// <summary>
    /// Lines added and removed per commit, newest first.
    /// </summary>
    /// <param name="fileExtensions">
    /// When given, only files of these types are diffed, so "lines added" covers the same files
    /// "total lines" does — and lock files, bundles and other uncounted files, usually the
    /// largest diffs in a repository, are not diffed at all.
    /// </param>
    /// <param name="onProgress">Called with (commits diffed, total) as the walk proceeds.</param>
    Task<IEnumerable<CommitStatsDto>> GetCommitStatsAsync(
        string repositoryPath,
        DateTime? sinceDate = null,
        IEnumerable<string>? fileExtensions = null,
        Action<int, int>? onProgress = null);
    Task<Dictionary<string, int>> CountLinesInCommitAsync(string repositoryPath, string commitSha, IEnumerable<string> fileExtensionsToCount);

    /// <summary>
    /// The text of every counted source file at one commit, subject to the same ignore rules the
    /// line counter applies.
    ///
    /// <para>Lazy and synchronous, deliberately. Lazy because a caller that materialised every
    /// file of a large repository would hold the whole checkout in memory at once, where measuring
    /// them one at a time holds one; synchronous because LibGit2Sharp is, and wrapping each blob
    /// read in a Task would add machinery around work that never yields. Callers doing this off a
    /// request thread should wrap the whole enumeration in one <c>Task.Run</c>.</para>
    ///
    /// <para>The git repository stays open for as long as the enumeration does, so it must be
    /// enumerated to completion or disposed.</para>
    /// </summary>
    IEnumerable<SourceFile> EnumerateSourceFiles(string repositoryPath, string commitSha, IEnumerable<string> fileExtensionsToCount);

    /// <summary>
    /// Per-path change counts from HEAD back to <paramref name="sinceUtc"/>, keyed by
    /// repository-relative path. Empty for a path that is not a valid clone.
    /// </summary>
    Task<IReadOnlyDictionary<string, FileChurn>> GetFileChurnAsync(string repositoryPath, DateTime sinceUtc);

    Task<IEnumerable<GitHubUserRepositoryDto>> GetUserRepositoriesAsync(string accessToken);
}
