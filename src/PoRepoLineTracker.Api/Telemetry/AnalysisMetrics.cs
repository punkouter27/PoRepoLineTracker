using System.Diagnostics.Metrics;
using PoRepoLineTracker.Shared.Domain;

namespace PoRepoLineTracker.Api.Telemetry;

/// <summary>
/// Per-commit telemetry: lines counted (cumulative across the loop) and the wall-clock duration
/// of a single commit's count.
///
/// <para>Reused the existing <see cref="AppTelemetry.Meter"/> rather than declaring a parallel
/// one: the OpenTelemetry pipeline in <c>TelemetryServiceExtensions.AddTelemetry</c> subscribes
/// only to <see cref="AppTelemetry.SourceName"/>, so a second meter would be silently dropped
/// by every exporter. New here is the per-repository tag (<c>repo</c>) and the dedicated record
/// entry points — the existing <c>LinesAnalyzed</c> counter does not tag by repository, which is
/// what the live view needs to render "throughput for this repo".</para>
/// </summary>
public static class AnalysisMetrics
{
    /// <summary>Public so unit tests can subscribe by name without reflection.</summary>
    public const string LinesCountedName = "analysis.lines.counted";

    /// <summary>Public so unit tests can subscribe by name without reflection.</summary>
    public const string CommitCountDurationName = "analysis.commit.count_duration_ms";

    private static readonly Counter<long> LinesCountedCounter =
        AppTelemetry.Meter.CreateCounter<long>(
            name: LinesCountedName,
            unit: "{line}",
            description: "Lines counted by the analysis loop, tagged by repository id.");

    private static readonly Histogram<double> CommitCountDuration =
        AppTelemetry.Meter.CreateHistogram<double>(
            name: CommitCountDurationName,
            unit: "ms",
            description: "Duration of a single commit's line-count pass, tagged by repository id.");

    /// <summary>
    /// Records <paramref name="lines"/> for <paramref name="repositoryId"/>. The tag uses the
    /// strongly-typed id's underlying Guid so dashboards can group by repo across runs.
    /// </summary>
    public static void RecordLinesCounted(RepositoryId repositoryId, long lines) =>
        LinesCountedCounter.Add(lines, new KeyValuePair<string, object?>("repo", repositoryId.Value.ToString()));

    /// <summary>
    /// Records the wall-clock duration of one commit's count pass, in milliseconds. Exposed as
    /// a histogram so the live view can render a p50/p95 alongside the running line tally.
    /// </summary>
    public static void RecordCommitCountDuration(RepositoryId repositoryId, double milliseconds) =>
        CommitCountDuration.Record(milliseconds, new KeyValuePair<string, object?>("repo", repositoryId.Value.ToString()));
}
