using Microsoft.Extensions.Caching.Hybrid;

namespace PoRepoLineTracker.API.Features.Repositories;

public record GetRepositoryHotspotsQuery(RepositoryId RepositoryId, int Days);

/// <summary>
/// Large files that keep changing: the files where a bug is most likely to be introduced next,
/// because they are both hard to hold in your head and constantly edited.
///
/// <para><b>Computed from the clone, like Code Health.</b> Stored commit rows carry per-language
/// totals, not per-file ones, and persisting per-file history would be a new table for one view.
/// Sizes come from the same walk and the same <see cref="CodeMetricsAnalyzer"/> Code Health
/// uses, so a file's line count agrees between the two tabs. Change counts come from
/// <c>git log --numstat</c>.</para>
///
/// <para>Returns null when the repository is unknown or has no analysed commit (the endpoint's
/// 404), and <see cref="HotspotsDto.HasClone"/> false when the clone is gone from disk.</para>
/// </summary>
public sealed class GetRepositoryHotspotsQueryHandler(
    IRepositoryDataService repositoryDataService,
    IGitHubService gitHubService,
    IUserPreferencesService userPreferencesService,
    HybridCache cache)
{
    /// <summary>What the treemap can draw legibly; the rest is summarised by <see cref="HotspotsDto.FilesChanged"/>.</summary>
    internal const int MaxFiles = 60;

    public async Task<HotspotsDto?> Handle(GetRepositoryHotspotsQuery request, CancellationToken cancellationToken = default)
    {
        var repository = await repositoryDataService.GetRepositoryByIdAsync(request.RepositoryId);
        if (repository is null) return null;

        var commits = await repositoryDataService.GetCommitLineCountsByRepositoryIdAsync(request.RepositoryId);
        var newest = commits.MaxBy(c => c.CommitDate);
        if (newest is null) return null;

        // Keyed on the SHA as well: a re-analysis that moves the newest commit is a new answer,
        // and a cached one for the old commit must not outlive it. An hour, because the detail
        // page renders every tab up front, so each visit asks; between re-analyses only the
        // window's start date moves, and an hour of drift on a months-long window is invisible.
        return await cache.GetOrCreateAsync(
            $"hotspots:{request.RepositoryId}:{request.Days}:{newest.CommitSha}",
            async ct => await BuildAsync(repository, newest.CommitSha, request.Days, ct),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(1), LocalCacheExpiration = TimeSpan.FromHours(1) },
            cancellationToken: cancellationToken);
    }

    private async Task<HotspotsDto> BuildAsync(GitHubRepository repository, string commitSha, int days, CancellationToken cancellationToken)
    {
        var result = new HotspotsDto { Days = days, CommitSha = commitSha };

        var repositoryPath = gitHubService.ResolveRepositoryPath(repository.LocalPath);
        if (!await gitHubService.IsRepositoryValidAsync(repositoryPath)) return result;
        result.HasClone = true;

        var churn = await gitHubService.GetFileChurnAsync(repositoryPath, DateTime.UtcNow.AddDays(-days));
        if (churn.Count == 0) return result;

        var extensions = repository.UserId != UserId.Empty
            ? await userPreferencesService.GetFileExtensionsAsync(repository.UserId)
            : UserPreferences.DefaultFileExtensions;

        // One Task.Run around the whole walk, as in GetCodeHealthQueryHandler: synchronous
        // LibGit2Sharp reads plus regex work, holding the repository open throughout.
        var files = await Task.Run(() => Rank(gitHubService.EnumerateSourceFiles(repositoryPath, commitSha, extensions), churn), cancellationToken);

        result.FilesChanged = files.Count;
        result.Files = files.Take(MaxFiles).ToList();
        return result;
    }

    /// <summary>
    /// Counted files still present at the commit and changed in the window, ranked by commits ×
    /// code lines. A product, not either alone: a huge file nobody touches and a tiny file edited
    /// daily are both low risk; the file that is both is the hotspot.
    /// </summary>
    internal static List<HotspotFileDto> Rank(IEnumerable<SourceFile> files, IReadOnlyDictionary<string, FileChurn> churn)
    {
        var analyzer = new CodeMetricsAnalyzer();
        var ranked = new List<HotspotFileDto>();
        foreach (var file in files)
        {
            if (!churn.TryGetValue(file.Path, out var change)) continue;

            var metrics = analyzer.Analyze(file.Path, file.Extension, file.Content);
            if (metrics.CodeLines == 0) continue;

            ranked.Add(new HotspotFileDto
            {
                Path = file.Path,
                CodeLines = metrics.CodeLines,
                Complexity = metrics.CyclomaticComplexity,
                Commits = change.Commits,
                LinesChanged = change.LinesChanged
            });
        }

        return ranked
            .OrderByDescending(f => (long)f.Commits * f.CodeLines)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
    }
}
