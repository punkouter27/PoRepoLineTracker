namespace PoRepoLineTracker.Api.Analysis;

/// <summary>
/// Turns raw <see cref="FileMetrics"/> into the scored report the UI renders.
///
/// <para><b>Why the thresholds are constants with names.</b> Every band below is a judgement call,
/// and the honest thing to do with a judgement call is to write it down where it can be argued
/// with rather than bury it in an expression. Each one is stated as "this value scores 100, this
/// value scores 0", and everything between is linear. They are calibrated so that ordinary,
/// healthy code lands in the 80s rather than at 100 — a metric that awards full marks to typical
/// code has no room left to distinguish good from excellent, and one that fails typical code gets
/// ignored.</para>
///
/// <para>Separate from <see cref="CodeMetricsAnalyzer"/> on purpose: measuring and judging are
/// different jobs with different reasons to change. Re-weighting the report must not risk altering
/// what was counted.</para>
/// </summary>
public static class CodeHealthScoring
{
    // ── Weights, in points out of 100 ────────────────────────────────────────────────────────
    //
    // Complexity carries the most because it is the factor most strongly associated with defect
    // density in the literature and the hardest to reverse once it accumulates. Documentation,
    // debt markers and line length carry least: they are real signals but cheap to fix, and a
    // project can be perfectly healthy while scoring poorly on any one of them.

    private const int ComplexityWeight = 30;
    private const int NestingWeight = 20;
    private const int FileSizeWeight = 20;
    private const int DocumentationWeight = 10;
    private const int DebtWeight = 10;
    private const int LineLengthWeight = 10;

    // ── Bands ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Decision points per 100 code lines. Around 10 is unremarkable; 35 is dense enough to be hard to follow.</summary>
    private const double ComplexityBest = 10, ComplexityWorst = 35;

    /// <summary>Deepest indent level in a code file. Three is a method inside a class; eight is a problem.</summary>
    private const double NestingBest = 3, NestingWorst = 8;

    /// <summary>
    /// The same for markup, which is legitimately far more indented — a Razor page is nested
    /// components inside a layout inside a container, and none of that is a smell.
    ///
    /// <para>Judging markup on the code band was a real miscalibration, not a rounding difference:
    /// this repository's own <c>.razor</c> files scored 48 against 94 for its C#, which told a
    /// reader nothing except that markup is indented. Deep markup IS worth flagging, so the factor
    /// is kept rather than skipped — it is the threshold that differs.</para>
    /// </summary>
    private const double MarkupNestingBest = 6, MarkupNestingWorst = 14;

    /// <summary>Extensions judged on the markup nesting band. CSS is excluded: it nests shallowly, like code.</summary>
    private static readonly string[] MarkupExtensions = [".razor", ".cshtml", ".html", ".xaml"];

    private static (double Best, double Worst) NestingBandFor(string extension) =>
        MarkupExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? (MarkupNestingBest, MarkupNestingWorst)
            : (NestingBest, NestingWorst);

    /// <summary>A file past this many code lines counts as large.</summary>
    private const int LargeFileLines = 400;

    /// <summary>Share of the codebase living in large files.</summary>
    private const double LargeShareBest = 0, LargeShareWorst = 0.50;

    /// <summary>Comment lines as a share of comment+code. Below the floor is under-documented; above the ceiling usually means commented-out code.</summary>
    private const double CommentFloor = 0.10, CommentCeiling = 0.35;

    /// <summary>Score awarded at the point where over-commenting stops being penalised further.</summary>
    private const int OverCommentedFloorScore = 60;
    private const double OverCommentedWorst = 0.70;

    /// <summary>Debt markers per 1,000 code lines.</summary>
    private const double DebtBest = 0, DebtWorst = 10;

    /// <summary>Share of lines over the long-line limit.</summary>
    private const double LongLineShareBest = 0, LongLineShareWorst = 0.15;

    /// <summary>Files listed as hotspots. Enough to be a work list, few enough to be read.</summary>
    private const int HotspotCount = 10;

    /// <summary>
    /// A file below this many code lines is never a hotspot. A four-line file with one `if` has a
    /// terrible complexity DENSITY and nothing wrong with it; without this floor the list fills
    /// with trivia and the real offenders are pushed off it.
    /// </summary>
    private const int HotspotMinimumLines = 30;

    /// <summary>
    /// The composite for a set of files, without building a whole report around it.
    ///
    /// <para>Exists so the per-language scores can be computed without re-entering
    /// <see cref="Build"/>. They used to call it recursively, and because <c>Build</c> itself
    /// derives a per-language breakdown, a single-extension group grouped to itself and recursed
    /// forever — a stack overflow that took the test host down rather than failing a test.</para>
    /// </summary>
    private static int ScoreOf(IReadOnlyList<FileMetrics> files)
    {
        var factors = BuildFactors(files);
        return factors.Count == 0 ? 0 : Composite(factors);
    }

    private static int Composite(List<CodeHealthFactorDto> factors) =>
        (int)Math.Round(factors.Sum(f => f.Score * (double)f.Weight) / factors.Sum(f => f.Weight));

    public static CodeHealthDto Build(IReadOnlyList<FileMetrics> files)
    {
        var report = new CodeHealthDto { HasData = files.Count > 0 };
        if (files.Count == 0) return report;

        var codeLines = files.Sum(f => (long)f.CodeLines);
        var commentLines = files.Sum(f => (long)f.CommentLines);

        report.FilesAnalyzed = files.Count;
        report.CodeLines = (int)Math.Min(codeLines, int.MaxValue);
        report.CommentLines = (int)Math.Min(commentLines, int.MaxValue);
        report.BlankLines = (int)Math.Min(files.Sum(f => (long)f.BlankLines), int.MaxValue);
        report.TotalComplexity = (int)Math.Min(files.Sum(f => (long)f.CyclomaticComplexity), int.MaxValue);
        report.DebtMarkers = (int)Math.Min(files.Sum(f => (long)f.DebtMarkers), int.MaxValue);

        // A repository of nothing but blank and comment lines has no code to judge.
        if (codeLines == 0)
        {
            report.HasData = false;
            return report;
        }

        report.Factors = BuildFactors(files);
        report.Score = Composite(report.Factors);
        report.Grade = GradeFor(report.Score);

        report.Hotspots = files
            .Where(f => f.CodeLines >= HotspotMinimumLines)
            .Select(ScoreFile)
            .OrderBy(f => f.Score)
            .ThenByDescending(f => f.CodeLines)
            .Take(HotspotCount)
            .ToList();

        report.ByLanguage = files
            .GroupBy(f => f.Extension, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CodeHealthLanguageDto
            {
                Extension = group.Key,
                Files = group.Count(),
                CodeLines = (int)Math.Min(group.Sum(f => (long)f.CodeLines), int.MaxValue),
                // ScoreOf, not Build — see the note on ScoreOf.
                Score = ScoreOf(group.ToList())
            })
            .Where(l => l.CodeLines > 0)
            .OrderByDescending(l => l.CodeLines)
            .ToList();

        return report;
    }

    /// <summary>
    /// The six scored factors for a set of files. Pure: it reads the metrics and returns the
    /// breakdown, with no report assembly and no recursion.
    /// </summary>
    private static List<CodeHealthFactorDto> BuildFactors(IReadOnlyList<FileMetrics> files)
    {
        var codeLines = files.Sum(f => (long)f.CodeLines);
        if (codeLines == 0) return [];

        var commentLines = files.Sum(f => (long)f.CommentLines);
        var totalComplexity = files.Sum(f => (long)f.CyclomaticComplexity);
        var debtMarkers = files.Sum(f => (long)f.DebtMarkers);

        var complexityDensity = totalComplexity / (codeLines / 100d);
        var averageNesting = files.Average(f => (double)f.NestingDepth);
        var largeFileShare = files.Where(f => f.CodeLines > LargeFileLines).Sum(f => (long)f.CodeLines) / (double)codeLines;
        var commentRatio = commentLines / (double)(commentLines + codeLines);
        var debtPerThousand = debtMarkers / (codeLines / 1000d);
        var longLineShare = files.Sum(f => (long)f.LongLines) / (double)codeLines;

        return
        [
            new CodeHealthFactorDto
            {
                Key = "complexity",
                Name = "Branch density",
                Score = Linear(complexityDensity, ComplexityBest, ComplexityWorst),
                Weight = ComplexityWeight,
                Measurement = $"{complexityDensity:N1} decision points per 100 lines",
                Explanation = "Counts branches and short-circuit operators in comment-stripped code. "
                            + "Dense branching is the factor most closely tied to defect rates and the hardest to unwind later, so it carries the most weight."
            },
            new CodeHealthFactorDto
            {
                Key = "nesting",
                Name = "Nesting depth",
                // Each file is scored against its OWN language's band and the SCORES are averaged
                // — not the depths. Averaging raw depths across a mixed codebase judges markup by
                // a code threshold, which is a fact about the file format rather than about the
                // code in it.
                Score = (int)Math.Round(files.Average(f =>
                {
                    var (best, worst) = NestingBandFor(f.Extension);
                    return (double)Linear(f.NestingDepth, best, worst);
                })),
                Weight = NestingWeight,
                Measurement = $"{averageNesting:N1} levels deep on average",
                Explanation = "The deepest indent level reached in each file. Depth is what makes code hard to hold in your head, independent of length. Markup is judged against a more generous threshold than code, because nested components are not a smell."
            },
            new CodeHealthFactorDto
            {
                Key = "file-size",
                Name = "File size",
                Score = Linear(largeFileShare, LargeShareBest, LargeShareWorst),
                Weight = FileSizeWeight,
                Measurement = $"{largeFileShare * 100:N0}% of code sits in files over {LargeFileLines:N0} lines",
                Explanation = $"Large files resist review and invite merge conflicts. Measured as the share of the codebase inside files over {LargeFileLines:N0} lines, not as a file count, so one enormous file is not hidden by many small ones."
            },
            new CodeHealthFactorDto
            {
                Key = "documentation",
                Name = "Documentation",
                Score = DocumentationScore(commentRatio),
                Weight = DocumentationWeight,
                Measurement = $"{commentRatio * 100:N0}% of lines are comments",
                Explanation = "Scored against a band rather than a maximum: too few comments leaves intent unrecorded, and a very high ratio usually means commented-out code rather than unusually good documentation."
            },
            new CodeHealthFactorDto
            {
                Key = "debt",
                Name = "Debt markers",
                Score = Linear(debtPerThousand, DebtBest, DebtWorst),
                Weight = DebtWeight,
                Measurement = $"{debtMarkers:N0} markers ({debtPerThousand:N1} per 1,000 lines)",
                Explanation = "TODO, FIXME, HACK, XXX and BUG. Self-reported debt: a low weight because writing one down is better behaviour than leaving it unmarked."
            },
            new CodeHealthFactorDto
            {
                Key = "line-length",
                Name = "Line length",
                Score = Linear(longLineShare, LongLineShareBest, LongLineShareWorst),
                Weight = LineLengthWeight,
                Measurement = $"{longLineShare * 100:N0}% of lines over 120 characters",
                Explanation = "Long lines hide code off the right edge in reviews and side-by-side diffs. Cheap to fix, so weighted lightly."
            }
        ];
    }

    /// <summary>
    /// Scores one file on the subset of factors that mean anything at file scale. Documentation
    /// and line length are left out: a single file legitimately has no comments, and judging one
    /// file on either would put well-written short files at the top of the work list.
    /// </summary>
    private static CodeHealthFileDto ScoreFile(FileMetrics file)
    {
        var density = file.CyclomaticComplexity / Math.Max(file.CodeLines, 1) * 100d;
        var complexity = Linear(density, ComplexityBest, ComplexityWorst);

        var (nestingBest, nestingWorst) = NestingBandFor(file.Extension);
        var nesting = Linear(file.NestingDepth, nestingBest, nestingWorst);
        var size = Linear(file.CodeLines, LargeFileLines, LargeFileLines * 3);
        var debt = Linear(file.DebtMarkers, 0, 10);

        var concerns = new (string Concern, int Score)[]
        {
            ("Dense branching", complexity),
            ("Deep nesting", nesting),
            ("Very large file", size),
            ("Unresolved debt markers", debt)
        };

        return new CodeHealthFileDto
        {
            Path = file.Path,
            CodeLines = file.CodeLines,
            CyclomaticComplexity = file.CyclomaticComplexity,
            NestingDepth = file.NestingDepth,
            DebtMarkers = file.DebtMarkers,
            Score = (int)Math.Round((complexity * 40 + nesting * 25 + size * 25 + debt * 10) / 100d),
            PrimaryConcern = concerns.OrderBy(c => c.Score).First().Concern
        };
    }

    /// <summary>
    /// 100 at <paramref name="best"/>, 0 at <paramref name="worst"/>, linear between, clamped
    /// outside. Works in either direction so a factor where lower is better reads the same way as
    /// one where higher is.
    /// </summary>
    private static int Linear(double value, double best, double worst)
    {
        if (Math.Abs(worst - best) < double.Epsilon) return 100;

        var position = (value - best) / (worst - best);
        return (int)Math.Round(Math.Clamp(1 - position, 0, 1) * 100);
    }

    /// <summary>
    /// Full marks inside the band, falling away on both sides — the one factor that is not
    /// monotonic, because more comments stop helping and start indicating commented-out code.
    /// </summary>
    private static int DocumentationScore(double commentRatio)
    {
        if (commentRatio < CommentFloor)
            return (int)Math.Round(commentRatio / CommentFloor * 100);

        if (commentRatio <= CommentCeiling) return 100;

        var overshoot = (commentRatio - CommentCeiling) / (OverCommentedWorst - CommentCeiling);
        return (int)Math.Round(100 - Math.Clamp(overshoot, 0, 1) * (100 - OverCommentedFloorScore));
    }

    private static string GradeFor(int score) => score switch
    {
        >= 90 => "A",
        >= 80 => "B",
        >= 70 => "C",
        >= 60 => "D",
        _ => "F"
    };

    /// <summary>
    /// The repository's single health figure, 0-100, folding the parsed C# and the estimated rest
    /// into one number.
    ///
    /// <para><b>Why one number was needed.</b> The two analysers were being shown side by side and
    /// they disagreed in public: one repository read "maintainability 62" next to grade A, another
    /// "67" next to grade F. Both were correct about different halves of their repository, and
    /// together they were useless - a reader cannot rank anything on two scales at once.</para>
    ///
    /// <para><b>Weighted by lines, not averaged.</b> A repository that is 95% C# and 5% shell must
    /// be described by its C#. Averaging the two composites equally would let a few hundred lines
    /// of YAML swing the verdict on fifty thousand lines of code - the same reason contributor
    /// share is weighted by lines added rather than averaged per commit.</para>
    ///
    /// <para><b>The calibration caveat.</b> The two inputs are not calibrated against each other:
    /// the Maintainability Index treats anything above 20 as healthy (Visual Studio colours it
    /// green there), while the heuristic composite is built so that 90 is an A. Blending them
    /// yields a number whose ORDERING is trustworthy - the thing this page exists for - while its
    /// absolute letter runs harsher for C#-heavy repositories than Visual Studio would call them.
    /// That is stated on the page rather than hidden.</para>
    /// </summary>
    /// <param name="maintainabilityIndex">Parsed C# index, or null when the repository holds no C#.</param>
    /// <param name="csharpLines">C# source lines, the weight behind <paramref name="maintainabilityIndex"/>.</param>
    /// <param name="heuristicScore">Composite over the other languages.</param>
    /// <param name="otherLines">Code lines behind <paramref name="heuristicScore"/>.</param>
    public static (int Score, string Grade) Combine(
        int? maintainabilityIndex, int csharpLines, int heuristicScore, int otherLines)
    {
        // Guarded: a repository can have C# whose lines did not register, and a zero weight on the
        // only present side would divide by nothing and report 0 for healthy code.
        var csharpWeight = maintainabilityIndex is not null ? Math.Max(csharpLines, 1) : 0;
        var otherWeight = otherLines > 0 ? otherLines : 0;
        var totalWeight = csharpWeight + otherWeight;

        if (totalWeight == 0) return (0, string.Empty);

        var weighted =
            (maintainabilityIndex ?? 0) * (double)csharpWeight
            + heuristicScore * (double)otherWeight;

        var score = (int)Math.Clamp(Math.Round(weighted / totalWeight), 0, 100);
        return (score, GradeFor(score));
    }
}
