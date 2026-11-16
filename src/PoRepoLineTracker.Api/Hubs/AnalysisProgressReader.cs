using Microsoft.AspNetCore.SignalR;
using PoRepoLineTracker.Api.Analysis;

namespace PoRepoLineTracker.Api.Hubs;

/// <summary>
/// Drains the bounded <see cref="AnalysisHub.ProgressChannel"/> and pushes each frame to the
/// owning user's SignalR group.
///
/// <para>The split exists so the analysis loop (the writer) never awaits a slow SignalR client.
/// The channel is the backpressure boundary; this reader is the single owner of the
/// <c>IHubContext&lt;AnalysisHub&gt;</c> dependency. Failures from a wedged client are swallowed
/// at the same granularity the old fire-and-forget <c>SendAsync</c> did — one dropped frame per
/// client, never one dropped analysis.</para>
///
/// <para>Owner resolution: the channel item is <c>(UserId, frame)</c>. The owner is not on the
/// DTO (that travels to the browser) and is not looked up here — by the time a job's last frame
/// is read, the progress service has already forgotten who owned it.</para>
/// </summary>
public sealed class AnalysisProgressReader(
    IHubContext<AnalysisHub> hubContext,
    ILogger<AnalysisProgressReader> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = AnalysisHub.ProgressChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    await SendAsync(item.Owner, item.Frame, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — the host is stopping, the channel reader throws on cancellation.
        }
    }

    private async Task SendAsync(UserId userId, Shared.Models.AnalysisProgressDto frame, CancellationToken stoppingToken)
    {
        try
        {
            await hubContext.Clients
                .Group(AnalysisHub.GroupFor(userId))
                .SendAsync(AnalysisHub.ProgressMethod, frame, stoppingToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Dropped progress push for repository {RepositoryId}", frame.RepositoryId);
        }
    }
}
