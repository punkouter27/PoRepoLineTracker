using System.Text.Json;

namespace PoRepoLineTracker.Api.Storage;

/// <summary>
/// Converts between a computed <see cref="CodeHealthDto"/> and the row that memoises it.
///
/// <para>Its own type rather than methods on the store, so the store stays about Table Storage and
/// this stays about the shape of a report. The two change for different reasons: adding a field to
/// the report must not mean editing the code that talks to Azure.</para>
/// </summary>
internal static class CodeHealthSnapshotSerializer
{
    /// <summary>
    /// Table Storage caps a string property at 64 KB. Held well under it: a report that would be
    /// truncated is better recomputed than stored in a form that deserializes into nonsense.
    /// </summary>
    private const int MaxReportBytes = 60_000;

    /// <summary>
    /// Reads a memoised report. Returns false — never throws — when the row predates a shape change
    /// or was stored empty: a bad memo must degrade to a recompute, never to a failed request.
    /// </summary>
    internal static bool TryRead(string json, out CodeHealthDto? report)
    {
        report = null;
        if (string.IsNullOrEmpty(json)) return false;

        try
        {
            report = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.CodeHealthDto);
            return report is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static CodeHealthSnapshotEntity ToEntity(RepositoryId repositoryId, string commitSha, CodeHealthDto report)
    {
        var entity = new CodeHealthSnapshotEntity
        {
            PartitionKey = repositoryId.ToString(),
            RowKey = commitSha,
            RepositoryId = repositoryId.Value,
            CommitSha = commitSha,
            CommitDate = report.CommitDate,
            Score = report.OverallScore,
            Grade = report.OverallGrade,

            // -1, not 0: Table Storage cannot tell an absent int from a zero one, and zero is a
            // real (terrible) maintainability index. "No C#" must not read as "unmaintainable C#".
            MaintainabilityIndex = report.Metrics?.MaintainabilityIndex ?? -1,

            CyclomaticComplexity = report.Metrics?.CyclomaticComplexity ?? report.TotalComplexity,
            LinesOfSourceCode = report.Metrics?.LinesOfSourceCode ?? report.CodeLines,
            LinesOfExecutableCode = report.Metrics?.LinesOfExecutableCode ?? 0,
            HeuristicScore = report.Score,
            FilesAnalyzed = report.FilesAnalyzed,
            MeasuredUtc = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(report, AppJsonSerializerContext.Default.CodeHealthDto);

        // Oversized reports keep their scalar columns and lose only the document, so the trend
        // still plots from this row even though the full card has to be rebuilt.
        entity.ReportJson = System.Text.Encoding.UTF8.GetByteCount(json) <= MaxReportBytes ? json : string.Empty;

        return entity;
    }
}
