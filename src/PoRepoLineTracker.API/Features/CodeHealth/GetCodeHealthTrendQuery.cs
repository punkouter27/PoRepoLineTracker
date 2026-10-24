using Microsoft.Extensions.Logging;

namespace PoRepoLineTracker.API.Features.CodeHealth;

/// <summary>One repository's health over its whole history (owner, 2026-10-24), month by month.</summary>
public record GetCodeHealthTrendQuery(RepositoryId RepositoryId);

/// <summary>
/// <para><b>Which commit a month is measured at.</b> The last commit on or before the 1st, UTC —
/// the state the code was actually in that morning. A month with no commit before it is skipped
/// rather than guessed at: inventing a point for a repository that did not exist yet would draw a
/// line through history that never happened. Consecutive months can resolve to the SAME commit when
/// nothing was pushed in between, and that is correct — the code did not change, so neither should
/// the score.</para>
///
/// <para><b>Why this is affordable at all.</b> Every figure is a pure function of the commit's tree,
/// so it is memoised per SHA and computed at most once ever. A first run pays for at most
/// <see cref="MaxPoints"/> snapshots (one a month for four years, then spread evenly over a longer
/// history); every run after that reads storage. A month resolving to a commit already measured,
/// in this run or an earlier one, reuses it, so a quiet year costs one measurement, not twelve.</para>
///
/// <para><b>The memo is loaded in ONE query, before the loop.</b> Reading it per commit inside the
/// loop is precisely the pattern that got <c>CommitExistsAsync</c> deleted — a point read per SHA
/// turning a long history into thousands of round-trips to answer what a single partition scan
/// already returns.</para>
/// </summary>
public sealed class GetCodeHealthTrendQueryHandler(
    IRepositoryDataService repositoryDataService,
    IGitHubService gitHubService,
    IUserPreferencesService userPreferencesService,
    ICodeHealthSnapshotStore snapshotStore,
    ILogger<GetCodeHealthTrendQueryHandler> logger)
{
    /// <summary>
    /// Most points on the line. Monthly up to four years of history; beyond that the boundaries are
    /// spread evenly, so a decade-old repository's first run measures 48 trees, not 120.
    /// </summary>
    internal const int MaxPoints = 48;

    public async Task<CodeHealthTrendDto?> Handle(GetCodeHealthTrendQuery request, CancellationToken cancellationToken = default)
    {
        var repository = await repositoryDataService.GetRepositoryByIdAsync(request.RepositoryId);
        if (repository is null) return null;

        var commits = (await repositoryDataService.GetCommitLineCountsByRepositoryIdAsync(request.RepositoryId))
            .OrderBy(c => c.CommitDate)
            .ToList();

        if (commits.Count == 0) return null;

        // A repository whose whole history sits inside one month has commits but crosses no
        // boundary, so there is nothing to plot — and that is NOT the same as having no commits.
        // Returning null here reported "no analysed commits yet" for a repository actively being
        // committed to, which is simply false. An empty series says "not enough history" instead.
        var months = ResolveMonthlyCommits(commits, DateTime.UtcNow);
        if (months.Count == 0)
        {
            return new CodeHealthTrendDto
            {
                RepositoryId = repository.Id,
                Owner = repository.Owner,
                Name = repository.Name,
                Points = []
            };
        }

        // One partition scan for the whole memo, before anything is measured.
        var memo = await snapshotStore.GetByRepositoryAsync(request.RepositoryId);

        var repositoryPath = gitHubService.ResolveRepositoryPath(repository.LocalPath);
        var hasClone = !string.IsNullOrWhiteSpace(repositoryPath) && Directory.Exists(repositoryPath);

        var extensions = repository.UserId != UserId.Empty
            ? await userPreferencesService.GetFileExtensionsAsync(repository.UserId)
            : UserPreferences.DefaultFileExtensions;

        var points = new List<CodeHealthTrendPointDto>(months.Count);
        var computed = 0;

        foreach (var (month, commit) in months)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (memo.TryGetValue(commit.CommitSha, out var cachedSnapshot))
            {
                points.Add(ToPoint(month, cachedSnapshot));
                continue;
            }

            // Nothing to read the tree from. The month is reported as a gap rather than dropped:
            // "we could not measure this" and "the repository was healthy" must not look alike.
            if (!hasClone) continue;

            try
            {
                var report = await Task.Run(
                    () => CodeHealthReportFactory.Build(
                        gitHubService.EnumerateSourceFiles(repositoryPath, commit.CommitSha, extensions)),
                    cancellationToken);

                report.CommitDate = commit.CommitDate;

                var entity = CodeHealthSnapshotSerializer.ToEntity(request.RepositoryId, commit.CommitSha, report);
                await snapshotStore.SaveAsync(entity);
                computed++;

                // So a later quiet month resolving to this same commit reads it rather than measuring
                // the identical tree again; the memo was loaded before the loop and knew nothing of it.
                memo[commit.CommitSha] = entity;

                points.Add(ToPoint(month, entity));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable commit is a gap in the line, not a failed chart. A shallow clone
                // in particular simply does not contain the older trees.
                logger.LogWarning(ex, "Could not measure {RepositoryId} at {Sha} for {Month:yyyy-MM}",
                    request.RepositoryId, commit.CommitSha, month);
            }
        }

        // Boundaries existed but none could be measured — a missing clone, or a shallow one that
        // does not contain the older trees. Still an empty series rather than a 404: the repository
        // is fine, the measurement is what is missing.
        if (points.Count == 0)
        {
            return new CodeHealthTrendDto
            {
                RepositoryId = repository.Id,
                Owner = repository.Owner,
                Name = repository.Name,
                Points = []
            };
        }

        logger.LogInformation(
            "Code health trend for {Owner}/{Name}: {Points} months, {Computed} newly measured, {Cached} from the memo",
            repository.Owner, repository.Name, points.Count, computed, points.Count - computed);

        return new CodeHealthTrendDto
        {
            RepositoryId = repository.Id,
            Owner = repository.Owner,
            Name = repository.Name,
            Points = points
        };
    }

    /// <summary>
    /// The commit each plotted 1st of the month is measured at: the last one on or before it. The
    /// boundaries run from the first 1st after the first commit to this month's; past
    /// <see cref="MaxPoints"/> they are thinned evenly, always keeping the first and the newest.
    ///
    /// <para>Walks the boundaries against a commit list already sorted by date, rather than scanning
    /// the whole history per boundary — the same reason the analysis handler pre-loads its SHA set
    /// instead of asking storage per commit. Months before the first commit never appear: the
    /// repository did not exist, and a zero would read as a verdict.</para>
    /// </summary>
    internal static List<(DateTime Month, CommitLineCount Commit)> ResolveMonthlyCommits(
        IReadOnlyList<CommitLineCount> ascendingByDate, DateTime now)
    {
        var result = new List<(DateTime, CommitLineCount)>();
        if (ascendingByDate.Count == 0) return result;

        var first = ascendingByDate[0].CommitDate;
        var firstBoundary = new DateTime(first.Year, first.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (firstBoundary < first) firstBoundary = firstBoundary.AddMonths(1);
        var lastBoundary = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var monthCount = (lastBoundary.Year - firstBoundary.Year) * 12 + lastBoundary.Month - firstBoundary.Month + 1;
        if (monthCount <= 0) return result;

        var boundaries = monthCount <= MaxPoints
            ? Enumerable.Range(0, monthCount)
            : Enumerable.Range(0, MaxPoints).Select(i => (int)Math.Round((double)i * (monthCount - 1) / (MaxPoints - 1)));

        var index = 0;
        CommitLineCount? latestSoFar = null;
        foreach (var month in boundaries.Select(offset => firstBoundary.AddMonths(offset)))
        {
            // Advance through the commits up to this boundary, keeping the last one seen.
            while (index < ascendingByDate.Count && ascendingByDate[index].CommitDate <= month)
            {
                latestSoFar = ascendingByDate[index];
                index++;
            }

            if (latestSoFar is not null) result.Add((month, latestSoFar));
        }

        return result;
    }

    private static CodeHealthTrendPointDto ToPoint(DateTime month, CodeHealthSnapshotEntity snapshot) => new()
    {
        Month = month,
        Score = snapshot.Score,
        Grade = snapshot.Grade,

        // -1 is the stored sentinel for "this commit held no C#"; it must not surface as an index.
        MaintainabilityIndex = snapshot.MaintainabilityIndex >= 0 ? snapshot.MaintainabilityIndex : null,

        CyclomaticComplexity = snapshot.CyclomaticComplexity,
        LinesOfSourceCode = snapshot.LinesOfSourceCode,
        CommitSha = GitClient.ShortSha(snapshot.CommitSha),
        CommitDate = snapshot.CommitDate
    };
}
