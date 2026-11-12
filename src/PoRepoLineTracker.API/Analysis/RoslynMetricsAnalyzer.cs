using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace PoRepoLineTracker.Api.Analysis;

/// <summary>
/// Visual Studio's Code Metrics for C#, computed from the Roslyn SYNTAX tree alone.
///
/// <para><b>What this is, against what the rest of the app does.</b> <see cref="CodeMetricsAnalyzer"/>
/// counts line-oriented proxies across a dozen languages — it never parses anything, which is what
/// lets it treat Python and Razor alike. This does the opposite for the one language it
/// understands: a real C# parse, producing the same figures the Code Metrics window reports. Both
/// exist because neither covers the other's case.</para>
///
/// <para><b>Why four metrics and not six.</b> Maintainability Index, Cyclomatic Complexity, Lines
/// of Source code and Lines of Executable code are all decidable from the syntax tree. Halstead
/// volume — the input that made the Maintainability Index look unreachable — is purely lexical:
/// it counts operator and operand TOKENS, so it needs no symbols. Depth of Inheritance and Class
/// Coupling genuinely do need a resolved compilation, because a base type or a referenced type can
/// live in another assembly, and nothing in a syntax tree can follow that.</para>
///
/// <para><b>And why the resolved compilation is not attempted.</b> Reaching it means MSBuildWorkspace,
/// which means the .NET SDK and MSBuild present at runtime and a restore of the repository being
/// measured. The deployed App Service is a runtime-only DOTNETCORE|10.0 container on an F1 plan —
/// no SDK, 1 GB of storage, a daily CPU quota — so it cannot build anything. Building an arbitrary
/// repository also EXECUTES that repository's code, through MSBuild targets and NuGet scripts,
/// which is not something a shared web host should do on a stranger's behalf. The MSBuildLocator
/// route additionally requires Microsoft.Build packages that currently carry high-severity
/// advisories, and this solution builds with TreatWarningsAsErrors. If those two metrics are ever
/// wanted, the honest way is an opt-in local agent, not a web request.</para>
///
/// <para><b>The Maintainability Index is per MEMBER, then averaged.</b> This is the one thing that
/// is easy to get wrong and produces a plausible-looking number when you do. The published formula
/// was calibrated on a single unit of code; fed repository-wide totals instead, the
/// <c>0.23 * complexity</c> term alone reaches several hundred and every repository of any size
/// scores 0. Measured per member and averaged, this repository scores 87 — which is the band
/// Visual Studio puts it in.</para>
/// </summary>
internal sealed class RoslynMetricsAnalyzer
{
    /// <summary>
    /// Members reported back as least maintainable. Enough to act on, short enough that the
    /// report stays a summary.
    /// </summary>
    private const int WorstMemberCount = 10;

    /// <summary>
    /// Analyses one C# source file, accumulating into <paramref name="accumulator"/>.
    /// Parse failures are not thrown: a file that does not parse is a fact about the repository
    /// (a partial checkout, a newer language version), not a fault in the report, and one bad file
    /// must not lose the other nine hundred.
    /// </summary>
    internal static void AnalyzeFile(string path, string content, RoslynMetricsAccumulator accumulator)
    {
        SyntaxNode root;
        SyntaxTree tree;
        try
        {
            tree = CSharpSyntaxTree.ParseText(SourceText.From(content), path: path);
            root = tree.GetRoot();
        }
        catch (Exception)
        {
            accumulator.FilesSkipped++;
            return;
        }

        accumulator.FilesAnalyzed++;

        // Lines of Source code: physical lines carrying at least one token. Blank lines and
        // comment-only lines hold no token, so they fall out without being special-cased.
        accumulator.SourceLines += DistinctLines(tree, root.DescendantTokens()
            .Where(t => !t.IsKind(SyntaxKind.EndOfFileToken))
            .Select(t => t.Span));

        // Lines of Executable code: lines holding a statement that actually runs. A block is
        // punctuation rather than execution — counting it would score every brace.
        accumulator.ExecutableLines += DistinctLines(tree, root.DescendantNodes()
            .OfType<StatementSyntax>()
            .Where(s => s is not BlockSyntax)
            .Select(s => s.Span));

        foreach (var member in root.DescendantNodes().Where(IsMeasurableMember))
        {
            var complexity = CyclomaticComplexity(member);
            var memberLines = DistinctLines(tree, member.DescendantTokens().Select(t => t.Span));
            var volume = HalsteadVolume(member);
            var index = MaintainabilityIndex(volume, complexity, memberLines);

            accumulator.MaintainabilitySum += index;
            accumulator.MemberCount++;
            accumulator.CyclomaticComplexity += complexity;

            accumulator.TrackWorst(new RoslynMemberMetric(
                Path.GetFileName(path),
                DescribeMember(member),
                (int)Math.Round(index),
                complexity,
                memberLines));
        }
    }

    /// <summary>
    /// The unit the Maintainability Index is defined over. Property accessors and local functions
    /// are included because both carry real logic that would otherwise be attributed to nothing —
    /// a heavily-branched getter is exactly the kind of member the index exists to surface.
    /// </summary>
    private static bool IsMeasurableMember(SyntaxNode node) =>
        node is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax;

    /// <summary>
    /// One for the entry point, plus one per decision. Boolean operators count for the same reason
    /// an <c>if</c> does: each is a branch the reader has to carry, whether or not it has braces.
    /// </summary>
    internal static int CyclomaticComplexity(SyntaxNode member)
    {
        var decisions = member.DescendantNodes().Count(n =>
            n is IfStatementSyntax or WhileStatementSyntax or ForStatementSyntax or ForEachStatementSyntax
              or DoStatementSyntax or CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax
              or CatchClauseSyntax or ConditionalExpressionSyntax or SwitchExpressionArmSyntax);

        var shortCircuits = member.DescendantTokens().Count(t =>
            t.IsKind(SyntaxKind.AmpersandAmpersandToken) || t.IsKind(SyntaxKind.BarBarToken)
            || t.IsKind(SyntaxKind.QuestionQuestionToken) || t.IsKind(SyntaxKind.QuestionQuestionEqualsToken));

        return 1 + decisions + shortCircuits;
    }

    /// <summary>
    /// Halstead volume: <c>length * log2(vocabulary)</c> over the member's tokens. Identifiers and
    /// literals are operands, every other token is an operator. Entirely lexical — no symbol is
    /// resolved, which is precisely why the Maintainability Index does not need a build.
    /// </summary>
    internal static double HalsteadVolume(SyntaxNode member)
    {
        long totalOperators = 0, totalOperands = 0;
        var distinctOperators = new HashSet<string>(StringComparer.Ordinal);
        var distinctOperands = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in member.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.EndOfFileToken) || token.IsKind(SyntaxKind.None)) continue;

            if (token.IsKind(SyntaxKind.IdentifierToken)
                || token.IsKind(SyntaxKind.NumericLiteralToken)
                || token.IsKind(SyntaxKind.StringLiteralToken)
                || token.IsKind(SyntaxKind.CharacterLiteralToken))
            {
                totalOperands++;
                distinctOperands.Add(token.ValueText);
            }
            else
            {
                totalOperators++;
                distinctOperators.Add(token.ValueText);
            }
        }

        var vocabulary = distinctOperators.Count + distinctOperands.Count;
        return vocabulary > 0 ? (totalOperators + totalOperands) * Math.Log2(vocabulary) : 0;
    }

    /// <summary>
    /// The published Maintainability Index, normalised to 0–100 the way Visual Studio reports it
    /// (the raw formula runs to 171). Clamped at zero: the raw value goes negative for genuinely
    /// awful code, and a negative maintainability reads as a bug rather than a verdict.
    /// </summary>
    internal static double MaintainabilityIndex(double halsteadVolume, int cyclomaticComplexity, int linesOfCode)
    {
        // Guarded because ln(0) is negative infinity, and a member CAN legitimately have neither
        // volume nor lines — an empty accessor, an abstract declaration.
        var safeVolume = halsteadVolume <= 0 ? 1 : halsteadVolume;
        var safeLines = linesOfCode <= 0 ? 1 : linesOfCode;

        var raw = 171
            - 5.2 * Math.Log(safeVolume)
            - 0.23 * cyclomaticComplexity
            - 16.2 * Math.Log(safeLines);

        return Math.Max(0, raw * 100 / 171);
    }

    /// <summary>
    /// Counts the distinct physical lines a set of spans touches. Distinct, because several tokens
    /// share a line and a per-token count would report the token total under a "lines" label.
    /// </summary>
    private static int DistinctLines(SyntaxTree tree, IEnumerable<TextSpan> spans)
    {
        var lines = new HashSet<int>();
        foreach (var span in spans) lines.Add(tree.GetLineSpan(span).StartLinePosition.Line);
        return lines.Count;
    }

    private static string DescribeMember(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.ValueText,
        ConstructorDeclarationSyntax c => $"{c.Identifier.ValueText} (constructor)",
        DestructorDeclarationSyntax d => $"{d.Identifier.ValueText} (finalizer)",
        OperatorDeclarationSyntax o => $"operator {o.OperatorToken.ValueText}",
        ConversionOperatorDeclarationSyntax => "conversion operator",
        LocalFunctionStatementSyntax l => $"{l.Identifier.ValueText} (local function)",
        AccessorDeclarationSyntax a => $"{EnclosingName(a)} ({a.Keyword.ValueText})",
        _ => node.Kind().ToString()
    };

    /// <summary>An accessor's own name is just "get"; the useful label is the property's.</summary>
    private static string EnclosingName(SyntaxNode accessor)
    {
        for (var node = accessor.Parent; node is not null; node = node.Parent)
        {
            if (node is PropertyDeclarationSyntax p) return p.Identifier.ValueText;
            if (node is IndexerDeclarationSyntax) return "this[]";
            if (node is EventDeclarationSyntax e) return e.Identifier.ValueText;
        }
        return "(accessor)";
    }

    internal const int WorstMemberLimit = WorstMemberCount;
}

/// <summary>One member's metrics, for the "worst offenders" list.</summary>
internal readonly record struct RoslynMemberMetric(
    string File,
    string Member,
    int MaintainabilityIndex,
    int CyclomaticComplexity,
    int LinesOfCode);

/// <summary>
/// Running totals across a repository's C# files.
///
/// <para>A mutable accumulator rather than a per-file result that gets summed, because the
/// Maintainability Index cannot be summed: it is an average over MEMBERS, and a per-file average
/// averaged again would weight a one-method file the same as a fifty-method one.</para>
/// </summary>
internal sealed class RoslynMetricsAccumulator
{
    internal int FilesAnalyzed { get; set; }
    internal int FilesSkipped { get; set; }
    internal int SourceLines { get; set; }
    internal int ExecutableLines { get; set; }
    internal int CyclomaticComplexity { get; set; }
    internal int MemberCount { get; set; }
    internal double MaintainabilitySum { get; set; }

    private readonly List<RoslynMemberMetric> _worst = [];

    /// <summary>
    /// Keeps only the least maintainable members. Held as a bounded list rather than every member,
    /// because a large repository has tens of thousands and the report shows ten.
    /// </summary>
    internal void TrackWorst(RoslynMemberMetric metric)
    {
        _worst.Add(metric);

        // Trimmed in batches rather than on every add: sorting a 2x buffer occasionally is cheaper
        // than maintaining order on each of tens of thousands of members.
        if (_worst.Count < RoslynMetricsAnalyzer.WorstMemberLimit * 4) return;

        _worst.Sort(static (a, b) => a.MaintainabilityIndex.CompareTo(b.MaintainabilityIndex));
        _worst.RemoveRange(RoslynMetricsAnalyzer.WorstMemberLimit, _worst.Count - RoslynMetricsAnalyzer.WorstMemberLimit);
    }

    internal IReadOnlyList<RoslynMemberMetric> WorstMembers()
    {
        _worst.Sort(static (a, b) => a.MaintainabilityIndex.CompareTo(b.MaintainabilityIndex));
        return _worst.Take(RoslynMetricsAnalyzer.WorstMemberLimit).ToList();
    }

    /// <summary>
    /// The repository's index: the mean of its members'. Returns null when nothing was measured,
    /// so the caller reports "no C# to measure" rather than the 100 an empty average would give.
    /// </summary>
    internal int? MaintainabilityIndex =>
        MemberCount > 0 ? (int)Math.Round(MaintainabilitySum / MemberCount) : null;
}
