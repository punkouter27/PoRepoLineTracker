using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PoRepoLineTracker.API.Features.Repositories;

/// <summary>
/// The one way an analysis gets run: a channel drained by a fixed pool of workers.
///
/// <para><b>What this replaces.</b> Bulk-add and re-analyse each started their job with a
/// fire-and-forget <c>Task.Run</c>. Nothing capped how many clones ran at once (a 200-repository
/// bulk-add from each of a few users ran them all in parallel), and a job that was queued but not
/// finished died with the process. A separate startup service then swept up only the rows that
/// were never attempted, so a lost re-analysis of an already-analysed repository was simply gone.</para>
///
/// <para><b>Durability.</b> A re-analysis stamps <see cref="GitHubRepository.AnalysisQueuedUtc"/>
/// on the row before it is queued, and the worker clears it after the job ends. A never-analysed
/// repository needs no stamp: having no attempt already marks it. The startup sweep queues
/// both kinds again. A job cancelled by shutdown keeps its stamp on purpose.</para>
/// </summary>
public sealed class AnalysisQueue(
    IServiceScopeFactory scopeFactory,
    ILogger<AnalysisQueue> logger) : BackgroundService
{
    /// <summary>
    /// ponytail: a fixed two analyses at a time per instance. Clone and history walk are disk- and
    /// CPU-heavy and the App Service plan is small; make it configurable if the plan grows, or move
    /// to Azure Storage Queues if the app ever scales out past one instance (this queue is per process).
    /// </summary>
    internal const int Workers = 2;

    // Hosted services start before the storage client has proven its table exists; the sweep's
    // first query would otherwise race app warm-up. Short, and only on the startup path.
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(3);

    private readonly Channel<AnalyzeRepositoryCommitsCommand> _channel =
        Channel.CreateUnbounded<AnalyzeRepositoryCommitsCommand>(new UnboundedChannelOptions { SingleReader = false });

    /// <summary>Queued or running. Keeps a second click from queueing the same repository twice.</summary>
    private readonly ConcurrentDictionary<RepositoryId, byte> _pending = new();

    /// <summary>
    /// Whether a job for this repository is queued or running. Re-analyse, delete and visibility
    /// changes answer 409 on it: a delete would leave a queued job cloning a repository that no
    /// longer exists, and the running job's final row write would undo a visibility change.
    /// </summary>
    public bool IsPending(RepositoryId repositoryId) =>
        _pending.ContainsKey(repositoryId) || AnalyzeRepositoryCommitsCommandHandler.IsRunning(repositoryId);

    /// <summary>Queues a run for a repository that has never been attempted. False if one is already pending.</summary>
    public bool Enqueue(RepositoryId repositoryId) => Write(new AnalyzeRepositoryCommitsCommand(repositoryId));

    /// <summary>
    /// Stamps the row, then queues a full re-analysis. False if one is already pending. The stamp
    /// comes first so that there is no moment where the job is queued but a restart would lose it.
    /// </summary>
    public async Task<bool> EnqueueReanalysisAsync(GitHubRepository repository, IRepositoryDataService repositoryDataService)
    {
        if (AnalyzeRepositoryCommitsCommandHandler.IsRunning(repository.Id) || !_pending.TryAdd(repository.Id, 0))
            return false;

        try
        {
            repository.AnalysisQueuedUtc = DateTime.UtcNow;
            repository.QueuedFullReanalysis = true;
            await repositoryDataService.UpdateRepositoryAsync(repository);
        }
        catch
        {
            _pending.TryRemove(repository.Id, out _);
            throw;
        }

        _channel.Writer.TryWrite(new AnalyzeRepositoryCommitsCommand(repository.Id, ClearExistingData: true));
        return true;
    }

    private bool Write(AnalyzeRepositoryCommitsCommand command)
    {
        if (AnalyzeRepositoryCommitsCommandHandler.IsRunning(command.RepositoryId) || !_pending.TryAdd(command.RepositoryId, 0))
            return false;

        // Unbounded, so TryWrite only fails once the writer is completed, which this type never does.
        _channel.Writer.TryWrite(command);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupGrace, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await ResumeUnfinishedAsync();
        await Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => WorkAsync(stoppingToken)));
    }

    /// <summary>
    /// Queues every row whose analysis a restart interrupted. Storage being unreachable at startup
    /// is logged, not fatal: failing the whole app over a resume sweep is the wrong trade, and
    /// new work still queues normally.
    /// </summary>
    private async Task ResumeUnfinishedAsync()
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dataService = scope.ServiceProvider.GetRequiredService<IRepositoryDataService>();
            var resumed = 0;
            foreach (var repository in await dataService.GetResumableRepositoriesAsync())
            {
                if (Write(new AnalyzeRepositoryCommitsCommand(repository.Id, repository.QueuedFullReanalysis)))
                    resumed++;
            }

            if (resumed > 0)
                logger.LogInformation("Resume sweep: queued {Count} unfinished analyses", resumed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resume sweep could not read unfinished analyses; skipping this startup");
        }
    }

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var command in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                await RunAsync(command, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. Whatever is still queued carries its stamp (or its lack of an attempt)
            // and is picked up by the next start's sweep.
        }
    }

    private async Task RunAsync(AnalyzeRepositoryCommitsCommand command, CancellationToken stoppingToken)
    {
        // A scope per job, not one per worker: jobs belong to many users, and the scoped
        // FileIgnoreFilter (custom globs) and GitHubService line-count memos would otherwise
        // carry one user's settings into the next user's analysis.
        using var scope = scopeFactory.CreateScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<AnalyzeRepositoryCommitsCommandHandler>()
                .Handle(command, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopped mid-run: leave the stamp so the next start resumes it.
            _pending.TryRemove(command.RepositoryId, out _);
            throw;
        }
        catch (Exception ex)
        {
            // The handler has already recorded the failure on the row and sent the terminal
            // frame; one broken repository must not stop the worker.
            logger.LogError(ex, "Analysis failed for repository {RepositoryId}: {Message}", command.RepositoryId, ex.Message);
        }

        // Clear the stamp BEFORE releasing the pending slot: in the other order a re-analysis
        // queued in between would have its fresh stamp wiped.
        await ClearQueuedStampAsync(scope.ServiceProvider.GetRequiredService<IRepositoryDataService>(), command.RepositoryId);
        _pending.TryRemove(command.RepositoryId, out _);
    }

    /// <summary>
    /// A fresh read, not the copy the handler held: the handler's row write happened minutes ago
    /// and anything changed since then would be overwritten by a write of that copy.
    /// Best-effort: a stamp left behind only means one extra (incremental) run on the next start.
    /// </summary>
    private async Task ClearQueuedStampAsync(IRepositoryDataService dataService, RepositoryId repositoryId)
    {
        try
        {
            var repository = await dataService.GetRepositoryByIdAsync(repositoryId);
            if (repository is null || (repository.AnalysisQueuedUtc is null && !repository.QueuedFullReanalysis)) return;

            repository.AnalysisQueuedUtc = null;
            repository.QueuedFullReanalysis = false;
            await dataService.UpdateRepositoryAsync(repository);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear the queued stamp on repository {RepositoryId}", repositoryId);
        }
    }
}
