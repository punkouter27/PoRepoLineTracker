namespace PoRepoLineTracker.Shared.Domain;

/// <summary>
/// Represents user-specific preferences for repository analysis.
/// </summary>
public record UserPreferences
{
    /// <summary>
    /// User ID this preference belongs to.
    /// </summary>
    public UserId UserId { get; init; }

    /// <summary>
    /// File extensions to include in line count calculations.
    /// Example: [".cs", ".razor", ".js", ".ts"]
    /// </summary>
    public List<string> FileExtensions { get; init; } = DefaultFileExtensions;

    /// <summary>
    /// Custom glob patterns to ignore during repository analysis (e.g. "**/migrations/**", "**/*.g.cs").
    /// </summary>
    public List<string> CustomIgnoreGlobs { get; init; } = [];

    /// <summary>
    /// Default file extensions for new users.
    /// Tuned for a typical Blazor WebAssembly + .NET API codebase:
    ///   .NET (server + Razor) + modern JS/TS frontend tooling + Python.
    /// Excludes generated/lock/config files on purpose (see FileIgnoreFilter).
    /// </summary>
    public static List<string> DefaultFileExtensions =>
    [
        // .NET server / Razor
        ".cs", ".razor", ".cshtml", ".xaml", ".csproj",
        // Modern JS/TS frontend
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs",
        // Web markup & styling
        ".html", ".css", ".scss", ".less",
        // Python
        ".py",
        // C and C++ (Unreal Engine projects; see FileIgnoreFilter for the folders it skips)
        ".cpp", ".h", ".hpp", ".cc", ".cxx", ".inl", ".c"
    ];

    /// <summary>
    /// When the preferences were last updated.
    /// </summary>
    public DateTime LastUpdated { get; init; } = DateTime.UtcNow;
}
