using Microsoft.Extensions.Logging;
using PoRepoLineTracker.API.Telemetry;
using PoRepoLineTracker.Shared.Models.Dtos;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace PoRepoLineTracker.API.Features.Repositories;

/// <summary>
/// Command to analyze commits for a repository.
/// </summary>
/// <param name="RepositoryId">The repository to analyze</param>
/// <param name="ClearExistingData">If true, delete all existing commit data and re-analyze from scratch</param>
public record AnalyzeRepositoryCommitsCommand(
    RepositoryId RepositoryId,
    bool ClearExistingData = false);

public class AnalyzeRepositoryCommitsCommandHandler
{
    private readonly IGitHubService _gitHubService;
    private readonly IRepositoryDataService _repositoryDataService;
    private readonly IUserService _userService;
    private readonly IUserPreferencesService _userPreferencesService;
    private readonly IAnalysisProgressService _progressService;
    private readonly ILogger<AnalyzeRepositoryCommitsCommandHandler> _logger;
    private readonly PoRepoLineTracker.API.Analysis.FileIgnoreFilter? _fileIgnoreFilter;

    // #10 fix: per-repository semaphore prevents git Checkout() race conditions on shared local path
    private static readonly ConcurrentDictionary<RepositoryId, SemaphoreSlim> _repoLocks = new();

    /// <summary>
    /// How many file types the live view is told about. It renders them as a row of chips that
    /// light up as each is discovered; past a dozen the row wraps into a wall and the tail is
    /// all single-file extensions nobody is watching for.
    /// </summary>
    private const int ReportedExtensionCount = 12;

    public AnalyzeRepositoryCommitsCommandHandler(
        IGitHubService gitHubService,
        IRepositoryDataService repositoryDataService,
        IUserService userService,
        IUserPreferencesService userPreferencesService,
        IAnalysisProgressService progressService,
        ILogger<AnalyzeRepositoryCommitsCommandHandler> logger,
        PoRepoLineTracker.API.Analysis.FileIgnoreFilter? fileIgnoreFilter = null)
    {
        _gitHubService = gitHubService;
        _repositoryDataService = repositoryDataService;
        _userService = userService;
        _userPreferencesService = userPreferencesService;
        _progressService = progressService;
        _logger = logger;
        _fileIgnoreFilter = fileIgnoreFilter;
    }

    /// <summary>
    /// The waits before the second and third clone/pull attempt, when the one before failed on
    /// the network (<see cref="AnalysisFailure.IsTransient"/>). A single dropped connection used
    /// to fail the whole run, and the user's only recourse was to click Re-analyze and hope.
    /// Settable so tests do not sleep.
    /// </summary>
    internal TimeSpan[] NetworkRetryDelays { get; init; } = [TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1)];

    /// <summary>
    /// Whether an analysis currently holds this repository's lock. The re-analyse and delete
    /// endpoints answer 409 on it: a second run would be silently skipped below while the caller
    /// was told "started", and a delete would leave the running loop upserting commit rows for a
    /// repository that no longer exists.
    /// </summary>
    public static bool IsRunning(RepositoryId repositoryId) =>
        _repoLocks.TryGetValue(repositoryId, out var semaphore) && semaphore.CurrentCount == 0;

    public async Task Handle(AnalyzeRepositoryCommitsCommand request, CancellationToken cancellationToken = default)
    {
        // #10 fix: if another analysis is already running for this repo, skip instead of racing
        var semaphore = _repoLocks.GetOrAdd(request.RepositoryId, _ => new SemaphoreSlim(1, 1));
        if (!await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            _logger.LogWarning("Analysis for repository {RepositoryId} already in progress — skipping concurrent request", request.RepositoryId);
            return;
        }

        try
        {
            await HandleInternalAsync(request, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task HandleInternalAsync(AnalyzeRepositoryCommitsCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Analyzing commits for repository ID: {RepositoryId} (ClearExistingData: {ClearExistingData})",
            request.RepositoryId, request.ClearExistingData);

        // Get the repository to analyze
        var repository = await _repositoryDataService.GetRepositoryByIdAsync(request.RepositoryId);
        if (repository == null)
        {
            _logger.LogWarning("Repository with ID {RepositoryId} not found", request.RepositoryId);
            _progressService.ReportError(request.RepositoryId, "Repository not found.");
            return;
        }

        // Opens the job and records its owner before any step is reported — the progress service
        // pushes each update to that user's connections and drops any report for a job whose
        // owner it does not know. Placed here rather than at the two endpoints that queue the
        // command because this is where the repository (and so its UserId) is actually loaded.
        _progressService.BeginJob(request.RepositoryId, repository.UserId, repository.Owner, repository.Name);

        try
        {
            // The token for the clone/pull: the owner's own, or the server PAT in Dev/Test only.
            // Inside the try: it is a Users-table read, and a failure after BeginJob must still
            // send the terminal frame and stamp the attempt, or the badge spins until the job ages out.
            var accessToken = await _userService.ResolveGitHubTokenAsync(repository.UserId);

            // ── Step 1: Clone/pull OR validate local repository ───────────────────────
            var repositoryPath = await EnsureRepositoryOnDiskWithRetryAsync(repository, accessToken, cancellationToken);

            // Clear existing commit data for a full re-analysis — only now that the repository is
            // known to be fetchable. This used to run before the clone/pull, so a revoked token, a
            // repository deleted on GitHub or a network failure wiped the user's whole history and
            // then failed, leaving nothing.
            if (request.ClearExistingData)
            {
                _logger.LogInformation("Clearing existing commit data for repository {RepositoryId} for full re-analysis", request.RepositoryId);
                await _repositoryDataService.DeleteCommitLineCountsForRepositoryAsync(request.RepositoryId);

                // Reset the last analyzed date so all commits are processed
                repository.LastAnalyzedCommitDate = null;
                await _repositoryDataService.UpdateRepositoryAsync(repository);
            }

            // Get user-specific file extensions to count (falls back to defaults if not configured)
            List<string> fileExtensionsToCount = UserPreferences.DefaultFileExtensions;
            if (repository.UserId != UserId.Empty)
            {
                var userPrefs = await _userPreferencesService.GetPreferencesAsync(repository.UserId);
                if (userPrefs != null)
                {
                    fileExtensionsToCount = userPrefs.FileExtensions;
                    if (_fileIgnoreFilter != null)
                    {
                        _fileIgnoreFilter.CustomIgnoreGlobs = userPrefs.CustomIgnoreGlobs;
                    }
                }
            }

            // ── Step 2: Fetch commit stats (incremental where possible) ──────────────
            var commitStatsList = await FetchAllCommitStatsAsync(repository, repositoryPath, request, fileExtensionsToCount, cancellationToken);

            // Pre-load existing commits once, before the loop. See the rationale on the method.
            // A full re-analysis has just emptied the partition, so there is nothing to load.
            var existingCommitsBySha = request.ClearExistingData
                ? new Dictionary<string, CommitLineCount>(StringComparer.Ordinal)
                : await PreloadExistingCommitsAsync(request.RepositoryId, cancellationToken);

            // ── Step 3: Process each commit ───────────────────────────────────────────
            await ProcessEachCommitAsync(
                repository, repositoryPath, request, fileExtensionsToCount,
                commitStatsList, existingCommitsBySha, cancellationToken);

            // ── Step 4: Save results ──────────────────────────────────────────────────
            await FinalizeRepositoryAsync(repository, commitStatsList);
        }
        // Not on shutdown: an attempt stamp would take a never-analysed repository out of the
        // startup sweep, so the job the restart interrupted would never be resumed.
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            _logger.LogError(ex, "Error analyzing repository {RepositoryId}", request.RepositoryId);
            _progressService.ReportError(request.RepositoryId, ex.Message);
            await RecordAttemptAsync(repository, ex.Message);
            throw; // Re-throw to let the API handle the error
        }
    }

    /// <summary>
    /// Stamps the outcome of a finished run on the repository row. The startup resume sweep keys
    /// on the attempt time: a repository that was tried and failed (deleted on GitHub, token
    /// revoked) or has no commits used to stay "pending" forever and was re-cloned on every
    /// restart. Best-effort — a storage failure here must not mask the error being recorded.
    /// </summary>
    private async Task RecordAttemptAsync(GitHubRepository repository, string? error)
    {
        repository.LastAnalysisAttemptUtc = DateTime.UtcNow;
        repository.LastAnalysisError = error;
        try
        {
            await _repositoryDataService.UpdateRepositoryAsync(repository);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record analysis outcome for repository {RepositoryId}", repository.Id);
        }
    }

    /// <summary>
    /// Step 1 with retries for network failures only. An auth failure or a missing repository
    /// fails at once: a revoked token is not fixed by waiting, and the stored error then says why.
    /// </summary>
    private async Task<string> EnsureRepositoryOnDiskWithRetryAsync(
        GitHubRepository repository,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await EnsureRepositoryOnDiskAsync(repository, accessToken);
            }
            catch (Exception ex) when (attempt < NetworkRetryDelays.Length && AnalysisFailure.IsTransient(ex.Message))
            {
                var delay = NetworkRetryDelays[attempt];
                _logger.LogWarning(ex, "Clone/pull of {RepositoryId} failed on the network; retrying in {Delay} (attempt {Next} of {Max})",
                    repository.Id, delay, attempt + 2, NetworkRetryDelays.Length + 1);
                _progressService.ReportStep(repository.Id, 1, "Cloning",
                    $"Step 1/4 — Network error, retrying in {delay.TotalSeconds:0}s (attempt {attempt + 2} of {NetworkRetryDelays.Length + 1})");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Step 1: bring the clone on disk up to date (pull-or-clone) and return its absolute path.
    ///
    /// <para>The clone URL is built here from Owner/Name, never read from the stored row or the
    /// request: the user's GitHub token is embedded in whatever URL git is handed, so a
    /// caller-controlled URL sent that token to a caller-controlled host.</para>
    /// </summary>
    private async Task<string> EnsureRepositoryOnDiskAsync(
        GitHubRepository repository,
        string? accessToken)
    {
        var repositoryId = repository.Id;
        if (!RepositoryValidators.IsGitHubName(repository.Owner) || !RepositoryValidators.IsGitHubName(repository.Name))
            throw new InvalidOperationException($"'{repository.Owner}/{repository.Name}' is not a valid GitHub repository name.");
        var cloneUrl = GitHubRepository.CloneUrlFor(repository.Owner, repository.Name);

        _progressService.ReportStep(repositoryId, 1, "Cloning",
            $"Step 1/4 — Cloning/pulling {repository.Owner}/{repository.Name}");
        _logger.LogInformation("[Step 1/4] Clone/pull for repository {RepositoryId}", repositoryId);

        var localPath = GitHubRepository.CloneDirectoryName(repositoryId);
        var absolutePath = _gitHubService.ResolveRepositoryPath(localPath);

        if (await _gitHubService.IsRepositoryValidAsync(absolutePath))
        {
            _logger.LogInformation("Pulling repository {Owner}/{Name} from {LocalPath}",
                repository.Owner, repository.Name, localPath);
            try
            {
                await _gitHubService.PullRepositoryAsync(localPath, accessToken);
            }
            catch (Exception pullEx)
            {
                _logger.LogWarning(pullEx,
                    "Pull failed for repository {RepositoryId} at {LocalPath} — deleting local copy and re-cloning",
                    repositoryId, localPath);
                await _gitHubService.DeleteLocalRepositoryAsync(localPath);
                await _gitHubService.CloneRepositoryAsync(cloneUrl, localPath, accessToken);
            }
        }
        else
        {
            _logger.LogInformation("Local path missing or invalid — cloning repository {Owner}/{Name} to {LocalPath}",
                repository.Owner, repository.Name, localPath);
            await _gitHubService.CloneRepositoryAsync(cloneUrl, localPath, accessToken);
        }

        // Clone and pull take the RELATIVE path: they own the base-directory convention, and
        // that relative form is what is persisted on the row — by the run's own row write
        // (the re-analysis reset, the finalize step, or the failure record), not a write of its own.
        repository.LocalPath = localPath;
        return absolutePath;
    }

    /// <summary>
    /// Step 2: fetch the commit list to process. Incremental for the ordinary run: once a
    /// repository has been analysed (<see cref="GitHubRepository.LastAnalyzedCommitDate"/> set),
    /// only commits newer than that date — minus a day of overlap for moved author dates
    /// (rebases, cherry-picks) — are walked and diffed. The wide 50-year window stays for
    /// never-analysed repositories (and a full re-analysis, which resets the date), which have
    /// no window to narrow to.
    ///
    /// <para>ponytail: the overlap is author-date based, so a cherry-pick carrying a much older
    /// author date than the last analysis can slip through until a full re-analysis; the SHA
    /// preload still keeps such a commit from being double-counted.</para>
    /// </summary>
    private async Task<List<CommitStatsDto>> FetchAllCommitStatsAsync(
        GitHubRepository repository,
        string repositoryPath,
        AnalyzeRepositoryCommitsCommand request,
        IEnumerable<string> fileExtensionsToCount,
        CancellationToken cancellationToken)
    {
        var repositoryId = request.RepositoryId;
        _progressService.ReportStep(repositoryId, 2, "Fetching",
            $"Step 2/4 — Fetching commit history for {repository.Owner}/{repository.Name}");
        _logger.LogInformation("[Step 2/4] Fetching commit stats for repository {RepositoryId}", repositoryId);

        var sinceDate = repository.LastAnalyzedCommitDate is { } lastAnalyzed
            ? lastAnalyzed.AddDays(-1)
            : DateTime.UtcNow.AddYears(-50);
        var commitStatsList = (await _gitHubService.GetCommitStatsAsync(
            repositoryPath,
            sinceDate,
            fileExtensionsToCount,
            // Same step, new description: the live view shows the count moving instead of a
            // bar that sits still for the length of the walk.
            (done, total) => _progressService.ReportStep(repositoryId, 2, "Fetching",
                $"Step 2/4 — Reading commit history ({done:N0} of {total:N0})"))).ToList();
        _logger.LogInformation("Found {CommitCount} commits to analyze for repository {RepositoryId} (since {Since:u})",
            commitStatsList.Count, repositoryId, sinceDate);
        _progressService.ReportCommitsFound(repositoryId, commitStatsList.Count);
        return commitStatsList;
    }

    /// <summary>
    /// Pre-load ALL existing commits in ONE query, before the per-commit loop.
    ///
    /// <para>This used to be gated on ForceReanalysis, which meant the ordinary incremental path
    /// — by far the common one — still asked storage "does this SHA exist?" once per commit
    /// inside the loop below. On a repository with a few thousand commits that is a few thousand
    /// Azure Table round-trips (each one also emitting two Information-level log lines) to
    /// answer a question one query already had the answer to, and it was the dominant cost of
    /// re-analysing an up-to-date repository: almost every commit is already stored, so almost
    /// every iteration paid for a round-trip and then did nothing.</para>
    ///
    /// <para>The whole set is what a single query returns anyway, so the gate saved nothing even
    /// when it applied.</para>
    /// </summary>
    private async Task<Dictionary<string, CommitLineCount>> PreloadExistingCommitsAsync(
        RepositoryId repositoryId,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Pre-loading existing commits for repository {RepositoryId}", repositoryId);
        var lookup = (await _repositoryDataService.GetCommitLineCountsByRepositoryIdAsync(repositoryId))
            // Duplicate SHAs are not supposed to exist — the SHA is the row key — but a
            // ToDictionary that throws here would abort the whole analysis over a storage
            // anomaly this loop is perfectly able to tolerate.
            .GroupBy(c => c.CommitSha, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _logger.LogDebug("Pre-loaded {Count} existing commits for repository {RepositoryId}",
            lookup.Count, repositoryId);
        return lookup;
    }

    /// <summary>
    /// Step 3: count lines per commit, store them, and feed live progress. Per-commit failures
    /// are caught and logged — they must not abort the run — but every other exception bubbles
    /// up so the outer try/catch reports it as a job error.
    /// </summary>
    private async Task ProcessEachCommitAsync(
        GitHubRepository repository,
        string repositoryPath,
        AnalyzeRepositoryCommitsCommand request,
        IEnumerable<string> fileExtensionsToCount,
        List<CommitStatsDto> commitStatsList,
        Dictionary<string, CommitLineCount> existingCommitsBySha,
        CancellationToken cancellationToken)
    {
        _progressService.ReportStep(request.RepositoryId, 3, "Processing",
            $"Step 3/4 — Processing {commitStatsList.Count} commits");
        _logger.LogInformation("[Step 3/4] Processing commits for repository {RepositoryId}", request.RepositoryId);

        // Live-view tallies, reported alongside the commit counts. Kept here rather than
        // recomputed by the progress service because this loop is the only place the
        // per-commit breakdown exists — the service holds one snapshot, not a history.
        long linesCountedSoFar = 0;
        var extensionTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var processedCount = 0;

        foreach (var commitStat in commitStatsList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (existingCommitsBySha.ContainsKey(commitStat.Sha))
            {
                _logger.CommitAlreadyProcessed(commitStat.Sha);
                continue;
            }

            try
            {
                // Per-commit telemetry: lines counted (tagged by repo) and the wall-clock
                // duration of one count pass. Both go into the AppTelemetry meter so the OTel
                // pipeline in AddTelemetry exports them; the live view's "throughput" reads
                // from CommitCountDuration, the digest's "since you were last here" reads from
                // LinesCounted.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var lineCounts = await _gitHubService.CountLinesInCommitAsync(repositoryPath, commitStat.Sha, fileExtensionsToCount);
                sw.Stop();
                var totalLines = lineCounts.Values.Sum();
                AnalysisMetrics.RecordLinesCounted(request.RepositoryId, totalLines);
                AnalysisMetrics.RecordCommitCountDuration(request.RepositoryId, sw.Elapsed.TotalMilliseconds);

                var commitLineCount = new CommitLineCount
                {
                    RepositoryId = request.RepositoryId,
                    CommitSha = commitStat.Sha,
                    CommitDate = commitStat.CommitDate,
                    TotalLines = totalLines,
                    LinesAdded = commitStat.LinesAdded,
                    LinesRemoved = commitStat.LinesRemoved,
                    LinesByFileType = lineCounts.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                    AuthorName = commitStat.AuthorName,
                    AuthorEmail = commitStat.AuthorEmail
                };

                linesCountedSoFar += totalLines;
                foreach (var (extension, lines) in lineCounts)
                    extensionTotals[extension] = extensionTotals.GetValueOrDefault(extension) + lines;

                await _repositoryDataService.AddCommitLineCountAsync(commitLineCount);
                _logger.ProcessedCommit(commitStat.Sha, totalLines, commitStat.LinesAdded, commitStat.LinesRemoved);

                processedCount++;
                if (processedCount % 5 == 0 || processedCount == commitStatsList.Count)
                {
                    // A fresh list each time: the progress DTO is serialized on a background
                    // task, so handing it a collection this loop keeps mutating would throw
                    // mid-send.
                    _progressService.ReportCommitProgress(
                        request.RepositoryId,
                        processedCount,
                        commitStatsList.Count,
                        linesCountedSoFar,
                        extensionTotals
                            .OrderByDescending(kv => kv.Value)
                            .Take(ReportedExtensionCount)
                            .Select(kv => kv.Key)
                            .ToList());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing commit {CommitSha} for repository {RepositoryId}",
                    commitStat.Sha, request.RepositoryId);

                // Surface the failure via telemetry (Serilog error above + OpenTelemetry counter).
                AppTelemetry.FailedOperations.Add(1,
                    new KeyValuePair<string, object?>("operation", "CommitProcessing"),
                    new KeyValuePair<string, object?>("repository.id", request.RepositoryId));
            }
        }
    }

    /// <summary>
    /// Step 4: bump the repository's "last analysed" timestamp to the newest commit processed,
    /// then signal completion. Without this update the UI keeps showing "Never analyzed" on a
    /// repository that has just been fully counted.
    /// </summary>
    private async Task FinalizeRepositoryAsync(
        GitHubRepository repository,
        List<CommitStatsDto> commitStatsList)
    {
        var repositoryId = repository.Id;
        _progressService.ReportStep(repositoryId, 4, "Saving",
            $"Step 4/4 — Saving results for {repository.Owner}/{repository.Name}");

        if (commitStatsList.Count > 0)
            repository.LastAnalyzedCommitDate = commitStatsList.Max(c => c.CommitDate);

        // One row write for the date and the attempt stamp. Not via the best-effort
        // RecordAttemptAsync: a lost LastAnalyzedCommitDate leaves the UI on "Never analyzed"
        // behind a "complete" frame, so a failure here must surface as the run's error.
        repository.LastAnalysisAttemptUtc = DateTime.UtcNow;
        repository.LastAnalysisError = null;
        await _repositoryDataService.UpdateRepositoryAsync(repository);
        _logger.LogInformation("Saved LastAnalyzedCommitDate {Date} for repository {RepositoryId}",
            repository.LastAnalyzedCommitDate, repositoryId);

        _progressService.ReportComplete(repositoryId);
        _logger.LogInformation("Completed analysis for repository ID: {RepositoryId}", repositoryId);
    }
}
