using System.Text.RegularExpressions;

namespace PoRepoLineTracker.API.Analysis;

/// <summary>
/// Static analysis of source text into maintainability metrics, in the spirit of Visual Studio's
/// Code Metrics — but line-oriented rather than parser-based, and honest about it.
///
/// <para><b>What this is not.</b> It is not a compiler front end. Visual Studio derives its
/// Maintainability Index from Halstead volume and a real control-flow graph, which needs a parser
/// per language and, for C#, a full compilation with references resolved. This app reads blobs
/// straight out of the git object store across a dozen languages and never builds anything, so a
/// parser-based metric is not available to it at any price it can pay. What IS available is the
/// text, and the text supports a set of well-understood proxies: branch-keyword density, nesting
/// depth, file size, comment ratio, debt markers, line length. Every one of them is a real signal
/// used by real tools; none of them is exact.</para>
///
/// <para><b>Why proxies are still worth computing.</b> The question a developer actually asks of a
/// score like this is "which of my files should I look at first", and the ranking these produce
/// answers it. The absolute number is a rough guide; the ordering is the useful part, and the
/// per-file breakdown is what the UI leads with for that reason.</para>
///
/// <para>Every figure below is arithmetic over the file's own characters, and the same input always yields the same
/// score.</para>
/// </summary>
public sealed class CodeMetricsAnalyzer
{
    /// <summary>
    /// A line longer than this is counted as over-long. 120 is the widest of the common house
    /// limits; using the most permissive one means a project with a stricter rule is not penalised
    /// for a limit this analyzer invented.
    /// </summary>
    private const int LongLineChars = 120;

    /// <summary>
    /// Spaces treated as one indent level. A tab counts as one level outright, so a tab-indented
    /// file is not scored as four times deeper than a space-indented one.
    /// </summary>
    private const int SpacesPerIndent = 4;

    /// <summary>
    /// Which percentile of a file's line depths is reported as its nesting.
    ///
    /// <para>NOT the maximum, which is what this used to report and which a single line can set.
    /// A wrapped argument list or a long string concatenation is indented far past the structure
    /// it sits in, so one such line made a file look deeply nested when nothing was: a 40-line
    /// middleware here, holding a wrapped CSP string, measured 14 levels deep with a cyclomatic
    /// complexity of 4 and was promoted onto the hotspot list ahead of genuinely tangled files.
    /// The 90th percentile still catches a file that is deep throughout and ignores a handful of
    /// outliers.</para>
    /// </summary>
    private const double NestingPercentile = 0.90;

    /// <summary>
    /// Decision points, by language family. Each occurrence adds one to a file's cyclomatic
    /// approximation, which starts at 1 for the single path through a file with no branches.
    ///
    /// <para>Matched with word boundaries against COMMENT-STRIPPED text, so prose containing the
    /// word "if" does not register. String literals still can — a limitation shared by every
    /// non-parser complexity tool, and the reason this is called an approximation throughout.</para>
    /// </summary>
    private static readonly Regex CStyleDecisions = new(
        @"\b(if|for|foreach|while|case|catch|when)\b|&&|\|\||\?\?|\?(?!\.)",
        RegexOptions.Compiled);

    private static readonly Regex PythonDecisions = new(
        @"\b(if|elif|for|while|except|and|or)\b",
        RegexOptions.Compiled);

    /// <summary>Markup has no control flow worth counting; only its structure and size matter.</summary>
    private static readonly string[] MarkupExtensions = [".html", ".xaml", ".csproj", ".css", ".scss", ".less"];

    private static readonly Regex DebtMarkers = new(
        @"\b(TODO|FIXME|HACK|XXX|BUG)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Measures one file. <paramref name="content"/> is the whole file; <paramref name="extension"/>
    /// selects the comment syntax and the decision-keyword set.
    /// </summary>
    public FileMetrics Analyze(string path, string extension, string content)
    {
        var syntax = CommentSyntax.For(extension);
        var decisions = DecisionsFor(extension);

        int codeLines = 0, commentLines = 0, blankLines = 0, longLines = 0, debt = 0;
        var complexity = 1;
        var depths = new List<int>();

        var inBlockComment = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.Length > LongLineChars) longLines++;
            debt += DebtMarkers.Matches(line).Count;

            var startedInComment = inBlockComment;
            var code = StripComments(line, syntax, ref inBlockComment);

            if (string.IsNullOrWhiteSpace(line))
            {
                blankLines++;
            }
            else if (string.IsNullOrWhiteSpace(code))
            {
                // Nothing but commentary survived — or the line sat inside a block comment.
                commentLines++;
            }
            else
            {
                codeLines++;
                depths.Add(NestingOf(line));

                // Only code counts toward complexity, and only when the line did not begin inside
                // a block comment — a commented-out `if` is not a branch.
                if (!startedInComment && decisions is not null)
                    complexity += decisions.Matches(code).Count;
            }
        }

        return new FileMetrics(
            path,
            extension,
            codeLines,
            commentLines,
            blankLines,
            complexity,
            PercentileOf(depths, NestingPercentile),
            longLines,
            debt);
    }

    /// <summary>
    /// Nearest-rank percentile of the given values, or 0 when there are none. Sorts a copy: the
    /// caller's list is the collection order, and nothing else depends on it, but sorting an
    /// argument in place is the kind of surprise that costs someone an afternoon later.
    /// </summary>
    private static int PercentileOf(List<int> values, double percentile)
    {
        if (values.Count == 0) return 0;

        var sorted = values.ToArray();
        Array.Sort(sorted);

        var rank = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    private static Regex? DecisionsFor(string extension)
    {
        if (MarkupExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return null;
        if (string.Equals(extension, ".py", StringComparison.OrdinalIgnoreCase)) return PythonDecisions;

        // Razor and cshtml carry C# in among the markup, so they get the C-style set.
        return CStyleDecisions;
    }

    /// <summary>
    /// Indent levels on a line, from its leading whitespace. A tab is one level; spaces are
    /// divided by <see cref="SpacesPerIndent"/>.
    ///
    /// <para>Indentation rather than brace counting, deliberately: braces do not exist in Python
    /// or in markup, and every language this app counts is conventionally indented to match its
    /// structure. A file that is badly formatted will measure badly here — which is arguably the
    /// right answer for a maintainability metric, but is a limitation either way.</para>
    /// </summary>
    private static int NestingOf(string line)
    {
        var spaces = 0;
        var tabs = 0;

        foreach (var character in line)
        {
            if (character == ' ') spaces++;
            else if (character == '\t') tabs++;
            else break;
        }

        return tabs + spaces / SpacesPerIndent;
    }

    /// <summary>
    /// Returns the code portion of a line, tracking whether it ends inside an unclosed block
    /// comment.
    ///
    /// <para>Deliberately the same shape as the stripping in <see cref="SourceLineCounter"/> and
    /// driven by the same <see cref="CommentSyntax"/> table, so the two cannot disagree about what
    /// is a comment. It is a separate implementation because this one must return the surviving
    /// text for keyword matching, where the counter only needs to know whether anything survived
    /// at all.</para>
    /// </summary>
    private static string StripComments(string line, CommentSyntax syntax, ref bool inBlockComment)
    {
        var working = line;

        if (inBlockComment)
        {
            if (syntax.Block is not { } activeBlock)
            {
                inBlockComment = false;
                return working;
            }

            var endIndex = working.IndexOf(activeBlock.End, StringComparison.Ordinal);
            if (endIndex < 0) return string.Empty;

            working = working[(endIndex + activeBlock.End.Length)..];
            inBlockComment = false;
        }

        if (syntax.Block is { } block)
        {
            while (true)
            {
                var startIndex = working.IndexOf(block.Start, StringComparison.Ordinal);
                if (startIndex < 0) break;

                var endIndex = working.IndexOf(block.End, startIndex + block.Start.Length, StringComparison.Ordinal);
                if (endIndex < 0)
                {
                    working = working[..startIndex];
                    inBlockComment = true;
                    break;
                }

                working = string.Concat(working.AsSpan(0, startIndex), working.AsSpan(endIndex + block.End.Length));
            }
        }

        if (syntax.LinePrefix is { } prefix)
        {
            var index = working.IndexOf(prefix, StringComparison.Ordinal);
            if (index >= 0) working = working[..index];
        }

        return working;
    }
}

/// <summary>Raw measurements for one file. Scoring happens separately — see CodeHealthScoring.</summary>
public sealed record FileMetrics(
    string Path,
    string Extension,
    int CodeLines,
    int CommentLines,
    int BlankLines,
    int CyclomaticComplexity,
    int NestingDepth,
    int LongLines,
    int DebtMarkers);
