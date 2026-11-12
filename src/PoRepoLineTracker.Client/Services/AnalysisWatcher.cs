using PoRepoLineTracker.Shared.Domain;
using PoRepoLineTracker.Shared.Models;
using PoRepoLineTracker.Shared.Serialization;

namespace PoRepoLineTracker.Client.Services;

/// <summary>
/// The fallback for when the SignalR hub cannot reach the browser.
///
/// <para>It decides nothing. It polls the API for repositories whose analysis someone is waiting
/// on, turns whatever the server says into the same <see cref="AnalysisProgressDto"/> frames
/// <see cref="AnalysisFeedClient"/> pushes, and hands them to <see cref="FrameReady"/>. So "a job
/// finished" is still handled in exactly one place, whether the news arrived by push or by poll —
/// this type is a second SOURCE of frames, never a second completion path.</para>
///
/// <para>This lived inline in <c>Repositories.razor</c>, which had grown to 992 lines by carrying
/// a timer, a cancellation source, a wait-list and a poll loop alongside its markup. Nothing about
/// the behaviour changes here; it is the same one loop, moved somewhere it can be read and tested
/// without a render tree around it.</para>
///
/// <para>Owned by the component that creates it — deliberately not a DI registration. The wait
/// list is page state, and a container-scoped instance would keep polling for a page the user
/// navigated away from.</para>
/// </summary>
public sealed class AnalysisWatcher(
    RepoApiClient api,
    AnalysisFeedClient feed,
    ILogger<AnalysisWatcher> logger) : IAsyncDisposable
{
    /// <summary>How often to ask the server, while the hub is unavailable.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Hard stop for the loop. A very large repository must not leave a timer running for the
    /// whole session.
    /// </summary>
    private static readonly TimeSpan MaxWatch = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Repositories being waited on, each with the <c>LastAnalysisAttemptUtc</c> it had when the
    /// wait began — that value changing is the completion signal. The server stamps it at the end
    /// of every run, success or failure. <c>LastAnalyzedCommitDate</c> used to be the signal, but a
    /// failed run never changes it and a re-analysis with no new commits restores the same value,
    /// so both left the wait open until the hour cap.
    /// </summary>
    private readonly Dictionary<RepositoryId, DateTime?> _awaiting = [];

    private readonly CancellationTokenSource _disposeCts = new();
    private bool _running;

    /// <summary>
    /// Receives every synthesised frame. The consumer is expected to be the same handler the hub
    /// feeds, so completion does identical work by either route.
    /// </summary>
    public Func<AnalysisProgressDto, Task>? FrameReady { get; set; }

    /// <summary>
    /// Starts waiting on one repository, given the <c>LastAnalysisAttemptUtc</c> it had BEFORE the
    /// job was queued.
    /// </summary>
    public void Watch(RepositoryId repositoryId, DateTime? baselineAttemptUtc)
    {
        _awaiting[repositoryId] = baselineAttemptUtc;
        StartIfNeeded();
    }

    /// <summary>
    /// Starts waiting on every repository that has never been attempted. Existing waits keep the
    /// baseline they were registered with — <see cref="Dictionary{TKey,TValue}.TryAdd"/>, not an
    /// indexer assignment, or re-calling this would reset the baseline of a job already in flight
    /// and its completion would never be recognised.
    /// </summary>
    public void WatchPending(IEnumerable<GitHubRepository> repositories)
    {
        foreach (var pending in repositories.Where(r => r.LastAnalyzedCommitDate is null && r.LastAnalysisAttemptUtc is null))
            _awaiting.TryAdd(pending.Id, null);

        StartIfNeeded();
    }

    /// <summary>Drops a repository from the wait list — called when the hub reports it finished.</summary>
    public void StopWatching(RepositoryId repositoryId) => _awaiting.Remove(repositoryId);

    /// <summary>
    /// Starts the loop if there is anything to wait on and the hub is not already carrying it.
    ///
    /// <para>The hub reports every step of every job the user owns, so polling on top of it would
    /// fetch the same data twice. This exists for the cases the hub cannot cover: a proxy that
    /// blocks WebSockets, or a failed handshake.</para>
    ///
    /// <para>Public so the owner can hook it to <see cref="AnalysisFeedClient.StateChanged"/>: a
    /// hub that drops after the wait was registered has to restart the poll too.</para>
    /// </summary>
    public void StartIfNeeded()
    {
        if (_running) return;
        if (feed.IsConnected) return;
        if (_awaiting.Count == 0) return;

        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        _running = true;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        cts.CancelAfter(MaxWatch);

        using var timer = new PeriodicTimer(PollInterval);
        logger.LogInformation("Fallback analysis poll started for {Count} repositories", _awaiting.Count);

        try
        {
            while (_awaiting.Count > 0 && await timer.WaitForNextTickAsync(cts.Token))
            {
                // The hub may come back mid-job — a reconnect is exactly what WithAutomaticReconnect
                // is for. Stand down when it does rather than duplicating its frames.
                if (feed.IsConnected)
                {
                    logger.LogInformation("Analysis feed reconnected — stopping fallback poll");
                    break;
                }

                try
                {
                    await PollTickAsync(cts.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Fallback poll tick failed — will retry");
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Fallback analysis poll cancelled");
        }
        finally
        {
            _running = false;
        }
    }

    private async Task PollTickAsync(CancellationToken cancellationToken)
    {
        var repos = await api.GetRepositoriesAsync(cancellationToken);

        if (repos is null) return;

        foreach (var (repositoryId, baseline) in _awaiting.ToList())
        {
            var updated = repos.FirstOrDefault(r => r.Id == repositoryId);

            // Deleted from under us — stop waiting rather than polling a 404 for an hour.
            if (updated is null)
            {
                _awaiting.Remove(repositoryId);
                continue;
            }

            // A changed attempt stamp is the completion signal, and the row's error says how it
            // ended. Checked BEFORE the progress endpoint because a finished job's progress entry
            // expires, and reading "no progress" as "still working" is what used to leave badges
            // spinning.
            if (updated.LastAnalysisAttemptUtc is not null && updated.LastAnalysisAttemptUtc != baseline)
            {
                await PublishTerminalFrameAsync(updated, updated.LastAnalysisError);
                continue;
            }

            // Still in flight. Mirror the running frame the hub would have pushed, so the step
            // name, commit counts and stuck/error flags reach the grid by the same route.
            AnalysisProgressDto? progress = null;
            try
            {
                progress = await api.GetAnalysisProgressAsync(repositoryId, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // 404 until the job registers itself. Not an error — just nothing to report yet.
            }

            if (progress is null) continue;

            if (progress.IsRunning)
                await EmitAsync(progress);
            else if (progress.ErrorMessage is not null)
                // Through the terminal path, not EmitAsync: a fast failure the poll never saw
                // running needs its running frame first, or the consumer drops the failure.
                await PublishTerminalFrameAsync(updated, progress.ErrorMessage);

            // A terminal frame with no error and no stamp change yet means the write has not
            // landed. Leave it outstanding and pick the completion up on a later tick.
        }
    }

    /// <summary>
    /// Synthesises the terminal frame the hub would have pushed, so completion does the same work
    /// by either path.
    /// </summary>
    private async Task PublishTerminalFrameAsync(GitHubRepository repo, string? error)
    {
        _awaiting.Remove(repo.Id);

        // The handler only acts on a running → finished transition, so a repository never seen
        // running needs its running frame first. Without this, the completion of a job the poll
        // only ever caught at the end would be silently dropped.
        if (!_seenRunning.Contains(repo.Id))
        {
            await EmitAsync(new AnalysisProgressDto
            {
                RepositoryId = repo.Id,
                Owner = repo.Owner,
                Name = repo.Name,
                IsRunning = true,
                StepName = "Analyzing",
                StepDescription = "Working…"
            });
        }

        await EmitAsync(new AnalysisProgressDto
        {
            RepositoryId = repo.Id,
            Owner = repo.Owner,
            Name = repo.Name,
            IsRunning = false,
            StepName = error is null ? "Complete" : "Failed",
            StepDescription = error ?? "Analysis complete",
            ErrorMessage = error
        });
    }

    /// <summary>
    /// Repositories this watcher has already reported as running. Tracked here rather than read
    /// back off the page's progress dictionary so the "needs a running frame first" decision does
    /// not depend on what the consumer happens to have kept.
    /// </summary>
    private readonly HashSet<RepositoryId> _seenRunning = [];

    private Task EmitAsync(AnalysisProgressDto frame)
    {
        if (frame.IsRunning) _seenRunning.Add(frame.RepositoryId);
        else _seenRunning.Remove(frame.RepositoryId);

        return FrameReady?.Invoke(frame) ?? Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeCts.CancelAsync();
        _disposeCts.Dispose();
    }
}
