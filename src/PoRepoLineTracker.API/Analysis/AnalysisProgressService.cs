using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using PoRepoLineTracker.Api.Hubs;
using PoRepoLineTracker.Shared.Models;

namespace PoRepoLineTracker.Api.Analysis;

/// <summary>
/// In-memory singleton that stores live analysis progress for background jobs.
/// Safe for concurrent access from multiple background Task.Run threads.
///
/// <para>Every mutation is also handed to the bounded channel that <see cref="AnalysisProgressReader"/>
/// drains, so the UI reflects a step change immediately instead of on the next poll. The reader
/// owns the SignalR <c>IHubContext</c>; the producer (this service) owns only the in-memory
/// snapshot and the writer half of the channel. That split is what keeps the analysis loop
/// non-blocking even when a SignalR client is wedged — see SPEC §10.</para>
/// </summary>
public sealed class AnalysisProgressService : IAnalysisProgressService
{
    private readonly ConcurrentDictionary<RepositoryId, AnalysisProgressDto> _progress = new();

    /// <summary>Owner of each tracked job — the address a push is sent to.</summary>
    private readonly ConcurrentDictionary<RepositoryId, UserId> _jobOwners = new();

    public void BeginJob(RepositoryId repositoryId, UserId userId, string owner, string name)
    {
        _jobOwners[repositoryId] = userId;

        // Replaced outright rather than mutated: a re-analysis of a repository that previously
        // failed would otherwise start out carrying the old ErrorMessage, and the UI renders a
        // non-empty ErrorMessage as a failure regardless of IsRunning.
        var dto = new AnalysisProgressDto
        {
            RepositoryId = repositoryId,
            Owner = owner,
            Name = name,
            IsRunning = true,
            StepIndex = 0,
            StepName = "Queued",
            StepDescription = $"Queued analysis for {owner}/{name}",
            // Stamped once, here, so a page that opens mid-analysis still knows when the job
            // began and can render elapsed time, throughput and an ETA.
            StartedUtc = DateTime.UtcNow,
            LastUpdatedUtc = DateTime.UtcNow
        };

        _progress[repositoryId] = dto;
        Publish(dto);
    }

    public void ReportStep(RepositoryId repositoryId, int stepIndex, string stepName, string stepDescription)
    {
        var dto = _progress.GetOrAdd(repositoryId, _ => new AnalysisProgressDto { RepositoryId = repositoryId });
        dto.StepIndex = stepIndex;
        dto.StepName = stepName;
        dto.StepDescription = stepDescription;
        dto.IsRunning = true;
        dto.ErrorMessage = null;
        dto.LastUpdatedUtc = DateTime.UtcNow;
        Publish(dto);
    }

    public void ReportCommitsFound(RepositoryId repositoryId, int total)
    {
        if (_progress.TryGetValue(repositoryId, out var dto))
        {
            dto.CommitsTotal = total;
            dto.CommitsProcessed = 0;
            dto.LastUpdatedUtc = DateTime.UtcNow;
            Publish(dto);
        }
    }

    public void ReportCommitProgress(
        RepositoryId repositoryId,
        int processed,
        int total,
        long linesCounted = 0,
        List<string>? extensions = null)
    {
        if (_progress.TryGetValue(repositoryId, out var dto))
        {
            dto.CommitsProcessed = processed;
            dto.CommitsTotal = total;
            dto.LinesCounted = linesCounted;
            // Assigned, never appended to: this object is serialized on a fire-and-forget task
            // while the loop keeps reporting, and mutating a live collection mid-send throws.
            if (extensions is not null) dto.Extensions = extensions;
            dto.LastUpdatedUtc = DateTime.UtcNow;
            Publish(dto);
        }
    }

    public void ReportComplete(RepositoryId repositoryId)
    {
        if (_progress.TryGetValue(repositoryId, out var dto))
        {
            dto.IsRunning = false;
            dto.LastUpdatedUtc = DateTime.UtcNow;
            Publish(dto);
        }

        // The snapshot stays for the polling endpoint, but the ownership entry does not: nothing
        // more will be sent for this job, and leaving it would grow the map by one per analysis
        // for the lifetime of the process.
        _jobOwners.TryRemove(repositoryId, out _);
    }

    public void ReportError(RepositoryId repositoryId, string errorMessage)
    {
        var dto = _progress.GetOrAdd(repositoryId, _ => new AnalysisProgressDto { RepositoryId = repositoryId });
        dto.IsRunning = false;
        dto.ErrorMessage = errorMessage;
        dto.LastUpdatedUtc = DateTime.UtcNow;
        Publish(dto);

        _jobOwners.TryRemove(repositoryId, out _);
    }

    public AnalysisProgressDto? GetProgress(RepositoryId repositoryId) =>
        _progress.TryGetValue(repositoryId, out var dto) ? dto : null;

    /// <summary>
    /// Hands a snapshot to the bounded channel that <see cref="AnalysisProgressReader"/> drains.
    ///
    /// <para>The channel is the backpressure boundary named in SPEC §10: a slow SignalR client
    /// cannot stall the analysis loop, because the writer is non-blocking (TryWrite) and the
    /// channel's <c>DropOldest</c> policy caps memory at 64 frames. Recovery on slow-consumer
    /// loss is the fallback poll in <c>Repositories.razor</c>, which synthesises the same
    /// frames from the persisted snapshot, so a lost frame costs at most one stale tick.</para>
    ///
    /// <para>A job with no recorded owner is not handed to the channel at all rather than
    /// broadcast to everyone — that is the case where <see cref="BeginJob"/> was skipped, and
    /// guessing would mean leaking one user's repository names to another.</para>
    /// </summary>
    private void Publish(AnalysisProgressDto dto)
    {
        if (!_jobOwners.TryGetValue(dto.RepositoryId, out var userId)) return;

        // TryWrite rather than WriteAsync: the producer is the analysis loop, which is the
        // thing we are protecting from backpressure. With DropOldest, TryWrite always succeeds
        // and the channel drops an older frame on its own; with Wait it would block, which is
        // the failure mode the channel exists to prevent.
        //
        // The owner rides along with the frame. The reader used to resolve it at read time, but
        // ReportComplete/ReportError remove the owner immediately after publishing — so the
        // final frame, the one that says "done", usually found no owner and was dropped.
        AnalysisHub.ProgressChannel.Writer.TryWrite((userId, dto));
    }
}
