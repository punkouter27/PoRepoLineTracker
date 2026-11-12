namespace PoRepoLineTracker.Api.Analysis;

/// <summary>
/// Turns a commit's source files into a report.
///
/// <para><b>Why this is its own type.</b> Two callers need it: the current report, and each point on
/// the monthly trend. Left inline in the first handler, the second would have grown its own copy of
/// the language split and the score combination — and a trend computed by slightly different
/// arithmetic than the headline figure is worse than no trend at all, because the chart and the card
/// would disagree about the same commit and both would look right.</para>
/// </summary>
internal static class CodeHealthReportFactory
{
    /// <summary>
    /// Builds the report for one commit's files.
    ///
    /// <para>C# is PARSED and everything else is ESTIMATED — never both over one file. Running the
    /// line-oriented proxies across C# as well would put two maintainability numbers on one
    /// repository, which is exactly the contradiction the combined score exists to remove.</para>
    /// </summary>
    internal static CodeHealthDto Build(IEnumerable<SourceFile> files)
    {
        var analyzer = new CodeMetricsAnalyzer();
        var heuristic = new List<FileMetrics>();
        var roslyn = new RoslynMetricsAccumulator();

        foreach (var file in files)
        {
            if (IsCSharp(file.Extension))
                RoslynMetricsAnalyzer.AnalyzeFile(file.Path, file.Content, roslyn);
            else
                heuristic.Add(analyzer.Analyze(file.Path, file.Extension, file.Content));
        }

        var report = CodeHealthScoring.Build(heuristic);
        report.Metrics = BuildMetricsReport(roslyn);

        // A C#-only repository gives the heuristic nothing, so Build reported "nothing to measure".
        // It measured plenty — through the parser instead.
        if (!report.HasData && roslyn.MemberCount > 0) report.HasData = true;

        (report.OverallScore, report.OverallGrade) = CodeHealthScoring.Combine(
            report.Metrics?.MaintainabilityIndex,
            report.Metrics?.LinesOfSourceCode ?? 0,
            report.Score,
            report.CodeLines);

        return report;
    }

    /// <summary>
    /// Razor is excluded even though it contains C#: the text on disk is not a compilation unit, and
    /// parsing it as C# yields a parse-error soup whose metrics would be noise wearing the label of
    /// a measurement.
    /// </summary>
    private static bool IsCSharp(string extension) =>
        string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Null rather than a report of zeroes when the commit held no C#, so the UI can say "no C# to
    /// measure" instead of showing a maintainability index of nothing.
    /// </summary>
    private static CodeMetricsReportDto? BuildMetricsReport(RoslynMetricsAccumulator accumulator)
    {
        if (accumulator.FilesAnalyzed == 0 && accumulator.FilesSkipped == 0) return null;

        return new CodeMetricsReportDto
        {
            MaintainabilityIndex = accumulator.MaintainabilityIndex,
            CyclomaticComplexity = accumulator.CyclomaticComplexity,
            LinesOfSourceCode = accumulator.SourceLines,
            LinesOfExecutableCode = accumulator.ExecutableLines,
            FilesAnalyzed = accumulator.FilesAnalyzed,
            FilesSkipped = accumulator.FilesSkipped,
            MembersMeasured = accumulator.MemberCount,
            LeastMaintainable = accumulator.WorstMembers().Select(m => new CodeMetricsMemberDto
            {
                File = m.File,
                Member = m.Member,
                MaintainabilityIndex = m.MaintainabilityIndex,
                CyclomaticComplexity = m.CyclomaticComplexity,
                LinesOfCode = m.LinesOfCode
            }).ToList()
        };
    }
}
