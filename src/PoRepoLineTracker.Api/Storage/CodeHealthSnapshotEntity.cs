using Azure;
using Azure.Data.Tables;

namespace PoRepoLineTracker.Api.Storage;

/// <summary>
/// One commit's code-health figures, memoised forever.
///
/// <para><b>Keyed on the commit, not on the month or the repository's "current" state.</b> A commit
/// is immutable: the tree at <c>abc1234</c> will contain the same bytes in a year, so every figure
/// derived from it — maintainability, complexity, line counts, the combined score — is a constant.
/// Keying on the SHA means the value is computed at most once ever, and the same row answers both
/// the repository's current report and any point on its monthly trend. Keying on the month instead
/// would have recomputed the same commit under a second name every time it happened to be the one
/// nearest a different boundary.</para>
///
/// <para><b>What it replaces.</b> Code health was computed on demand and thrown away, so opening a
/// report re-walked the whole tree and re-parsed every C# file — seconds each time, for an answer
/// that cannot have changed. A twenty-four-month chart made that twenty-four times worse. This is
/// the same reasoning that already puts <see cref="CommitLineCountEntity"/> in storage rather than
/// recounting lines per commit on every view.</para>
///
/// <para><b>It also outlives the clone.</b> Computing a report needs the working checkout; the
/// deployed F1 App Service has a small, recyclable filesystem, so the clone is often simply not
/// there. Once a commit's row is written the figure is answerable from storage with no clone at
/// all — the difference between a chart that works on Azure and one that does not.</para>
///
/// <para>Partitioned by repository so the whole memo loads in ONE query. Reading it per commit
/// inside a loop is exactly the mistake that got <c>CommitExistsAsync</c> deleted: a point read per
/// SHA turned a 4,000-commit repository into 4,000 round-trips.</para>
/// </summary>
public class CodeHealthSnapshotEntity : ITableEntity
{
    public string PartitionKey { get; set; } = default!; // RepositoryId (as string)

    /// <summary>The commit SHA. The figures below are a pure function of it.</summary>
    public string RowKey { get; set; } = default!;

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public Guid RepositoryId { get; set; }
    public string CommitSha { get; set; } = string.Empty;
    public DateTime CommitDate { get; set; }

    /// <summary>The combined 0-100 figure. See <c>CodeHealthScoring.Combine</c>.</summary>
    public int Score { get; set; }

    public string Grade { get; set; } = string.Empty;

    /// <summary>
    /// Parsed C# maintainability index, or -1 when the commit held no C#. A sentinel rather than a
    /// nullable because Table Storage stores absence and zero identically, and zero is a real
    /// (terrible) index — "no C#" and "unmaintainable C#" must not collapse into one value.
    /// </summary>
    public int MaintainabilityIndex { get; set; } = -1;

    public int CyclomaticComplexity { get; set; }
    public int LinesOfSourceCode { get; set; }
    public int LinesOfExecutableCode { get; set; }

    /// <summary>Heuristic composite over the non-C# source at this commit.</summary>
    public int HeuristicScore { get; set; }

    /// <summary>Files the line-oriented analyser measured; C# files are counted separately.</summary>
    public int FilesAnalyzed { get; set; }

    /// <summary>When this row was computed, so a memo written by older scoring logic can be spotted.</summary>
    public DateTime MeasuredUtc { get; set; }

    /// <summary>
    /// The whole report — factors, hotspots, per-language breakdown, least-maintainable members —
    /// serialized, so a repeat view is answered entirely from storage rather than by re-walking the
    /// tree. The scalar columns above are kept alongside it so the monthly trend can plot twenty-four
    /// points without deserializing twenty-four documents.
    ///
    /// <para>Empty when the report was too large for a Table Storage string property (64 KB). That
    /// costs a recompute for that one commit rather than failing the write and losing the row's
    /// scalars too — a partial memo still saves the trend.</para>
    /// </summary>
    public string ReportJson { get; set; } = string.Empty;

    /// <summary>
    /// Version of the scoring logic that produced this row. Bumped when the arithmetic changes, so
    /// stale figures can be recomputed rather than silently mixed with new ones on the same chart —
    /// a memo is only safe while the function it memoises is unchanged.
    /// </summary>
    public int ScoringVersion { get; set; }
}
