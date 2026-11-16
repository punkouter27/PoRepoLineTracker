using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PoRepoLineTracker.Api.Telemetry;

/// <summary>
/// Centralized telemetry instrumentation for the Application layer.
/// Defines ActivitySource for distributed tracing and Meter for custom metrics.
/// </summary>
public static class AppTelemetry
{
    public const string SourceName = "PoRepoLineTracker";
    public const string Version = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(SourceName, Version);
    public static readonly Meter Meter = new(SourceName, Version);

    // Counters
    public static readonly Counter<long> RepositoriesAdded = Meter.CreateCounter<long>(
        "repositories.added",
        unit: "{repository}",
        description: "Total number of repositories added to the system");

    public static readonly Counter<long> FailedOperations = Meter.CreateCounter<long>(
        "operations.failed",
        unit: "{operation}",
        description: "Total number of failed operations (analysis, clone, storage)");

    // Histograms
    public static readonly Histogram<double> AddRepositoryDuration = Meter.CreateHistogram<double>(
        "repository.add_duration",
        unit: "ms",
        description: "Duration of add repository operations");

    // Eight more instruments used to be declared here (commits/lines analysed, clone and
    // analysis durations, GitHub API latency and calls) and were never recorded anywhere.
    // AnalysisMetrics carries the per-commit measurements that are.
    //
    // The observable gauges that used to live here were wired to constant-zero callbacks, so
    // every scrape reported 0 repositories forever. Deleted rather than left lying: a metric
    // that always reads zero is worse than no metric.
}
