namespace PoRepoLineTracker.API.Analysis;

/// <summary>
/// How a language marks comments, and the one table that says which language uses what.
///
/// <para><b>Why this is not private to the line counter.</b> Two things now need to know where the
/// code stops and the commentary starts: <see cref="SourceLineCounter"/>, which discards comments
/// so a total reflects authored code, and <see cref="CodeMetricsAnalyzer"/>, which counts them as
/// a documentation signal and must strip them before looking for branch keywords (a
/// <c>// if we ever need to</c> in prose is not a decision point). A second copy of the table
/// would drift, and the drift would be invisible: both sides would still produce plausible
/// numbers.</para>
///
/// <para>The table already had a history of exactly that. Extensions were registered one line at a
/// time, and two of them named a syntax the language does not have — <c>.css</c> and <c>.razor</c>
/// were given C-style <c>//</c> line comments, so a CSS <c>url(https://…)</c> was truncated at its
/// scheme. Grouping by family is what makes a wrong entry visible.</para>
/// </summary>
/// <param name="LinePrefix">Marker that comments out the rest of the line, or null if the language has none.</param>
/// <param name="Block">Opening and closing delimiters of a multi-line comment, or null if the language has none.</param>
public sealed record CommentSyntax(string? LinePrefix, (string Start, string End)? Block)
{
    /// <summary>No comment syntax at all — every non-blank line is content. The fallback for unknown extensions.</summary>
    public static readonly CommentSyntax None = new(null, null);

    /// <summary>
    /// Extensions grouped by the syntax they actually use. Adding a language means adding it to
    /// the family it belongs to, not copying a line and hoping the right one was copied.
    /// </summary>
    private static readonly (string[] Extensions, CommentSyntax Syntax)[] Families =
    [
        // C-style: // line comments, /* */ blocks.
        ([".cs", ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs",
          ".cpp", ".h", ".hpp", ".cc", ".cxx", ".inl", ".c"], new CommentSyntax("//", ("/*", "*/"))),

        // CSS has NO line-comment syntax — only /* */. Registered with "//" it truncated every
        // line from the first "//" onward, so `url(https://…)` lost its tail and a line holding
        // nothing else counted as blank.
        ([".css"], new CommentSyntax(null, ("/*", "*/"))),

        // Sass and Less DO have "//", unlike plain CSS. Same blocks, different line rule — which
        // is precisely why they cannot share an entry with .css.
        ([".scss", ".less"], new CommentSyntax("//", ("/*", "*/"))),

        // Markup: <!-- --> only. Razor's own comments are @* *@; "//" is not one, so a line
        // carrying an href or an xmlns used to be cut short.
        ([".razor", ".cshtml", ".html", ".xaml", ".csproj"], new CommentSyntax(null, ("<!--", "-->"))),

        ([".py"], new CommentSyntax("#", null))
    ];

    private static readonly Dictionary<string, CommentSyntax> ByExtension = BuildLookup();

    private static Dictionary<string, CommentSyntax> BuildLookup()
    {
        var lookup = new Dictionary<string, CommentSyntax>(StringComparer.OrdinalIgnoreCase);
        foreach (var (extensions, syntax) in Families)
            foreach (var extension in extensions)
                lookup[extension] = syntax;
        return lookup;
    }

    /// <summary>Every extension with a known syntax, for callers that need to enumerate them.</summary>
    public static IEnumerable<string> KnownExtensions => ByExtension.Keys;

    /// <summary>
    /// The syntax for an extension, or <see cref="None"/> when it is not known. Never null: an
    /// unrecognised language is counted verbatim rather than being skipped, which is the safe
    /// direction to be wrong in.
    /// </summary>
    public static CommentSyntax For(string extension) =>
        ByExtension.GetValueOrDefault(extension, None);
}
