using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileSystemGlobbing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PoRepoLineTracker.Api.Analysis;

/// <summary>
/// Filters files and directories that should be excluded from line counting.
/// Consumed directly by <see cref="GitHubService"/> — the interface it used to sit behind had a
/// single implementation, a single injection site, and was never substituted in a test.
/// </summary>
public class FileIgnoreFilter
{
    private readonly ILogger<FileIgnoreFilter> _logger;

    // Combined sets for faster lookup
    private readonly HashSet<string> _exactFileNames;
    private readonly HashSet<string> _extensionSuffixes;
    private readonly HashSet<string> _filePatterns;
    private readonly HashSet<string> _bundledAssetPrefixes;
    private readonly HashSet<string> _directoryPatterns;
    private readonly HashSet<string> _rootOnlyDirectoryNames;
    private const string MigrationFolder = "migrations/";
    private static readonly string[] GeneratedPathFragments =
    [
        "/_framework/",
        "/_content/",
        "/wwwroot/wwwroot/",
        "/site/wwwroot/"
    ];

    // Unity (and Java) vendor packages are named with a reverse-domain identifier —
    // com.unity.ml-agents, com.google.firebase, and so on. Nobody names their own app code that
    // way, so a directory segment matching this shape is treated as a vendored package regardless
    // of where in the tree it sits — see FileIgnoreFilterTests for the case this was added for
    // (a repo vendoring com.unity.ml-agents under a custom "Training/" folder rather than Unity's
    // standard Packages/ directory, where it would have already been caught by name).
    private static readonly Regex ReverseDomainPackageDirectory =
        new(@"(^|/)com\.[a-z0-9_-]+\.[a-z0-9_.-]+(/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ─── Embedded repository roots ───────────────────────────────────────────────────────────
    //
    // Marker files that only ever sit at the ROOT of a distinct project. A subdirectory carrying
    // several of these is not a subdirectory of this project — it is somebody else's repository
    // that was copied in wholesale.
    //
    // This is the rule that catches the case the reverse-domain regex above was reaching for and
    // mostly missed. A repo vendoring Unity's ml-agents under `Training/ml-agents/` had ~587
    // source files there; the regex caught the 257 inside `com.unity.ml-agents/` and left the
    // other 330 — the whole Python trainer package, ML-Agents' own Unity samples and tests, its
    // CI scripts — counted as the author's own code. Measured on one real repository, that made
    // third-party code roughly 78% of the reported line count.
    //
    // Naming the vendored folder would not generalise: `Training/ml-agents` is a name the author
    // chose, and the next repo will choose a different one. What does not vary is that a copied
    // project brings its own licence, its own CI config and its own community files.
    private static readonly HashSet<string> RepositoryRootMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".github", ".gitmodules", ".gitattributes", ".pre-commit-config.yaml",
        "codeowners", "code_of_conduct.md", "contributing.md", "governance.md",
        "license", "license.md", "license.txt", "licence", "licence.md", "copying",
        "third party notices.md", "notice", "notice.txt",
    };

    // Two, not one. A single stray LICENSE.md next to a bundled asset should not take out a
    // directory the author actually wrote, and real vendored roots carry a whole cluster of these
    // — the ml-agents copy above has six. Requiring a pair keeps the rule specific enough that
    // this project's own `src/PoRepoLineTracker.Api/` (no licence, no .github, no CODEOWNERS)
    // cannot trip it.
    private const int RepositoryRootMarkerThreshold = 2;

    public FileIgnoreFilter(ILogger<FileIgnoreFilter> logger)
    {
        _logger = logger;

        // Exact file names to ignore (case-insensitive)
        _exactFileNames =
        [
            "packages.config", "package-lock.json", "yarn.lock",
            "paket.lock", "paket.dependencies", "launchsettings.json"
        ];

        // Extension suffixes to ignore
        _extensionSuffixes =
        [
            // Build output
            ".dll", ".exe", ".pdb", ".obj", ".cache", ".lib", ".exp", ".ilk", ".idb", ".nupkg",
            // Auto-generated (.generated.h: Unreal Header Tool output, only present if committed)
            ".designer.cs", ".g.cs", ".g.i.cs", ".designer.vb", ".g.vb", ".generated.h",
            // Third-party minified
            ".min.js", ".min.css",
            // IDE config
            ".user", ".suo", ".vspscc", ".vssscc",
            // Web assets
            ".woff", ".woff2", ".ttf", ".eot", ".otf",
            // Compiled resources
            ".resources"
        ];

        // File name patterns to ignore (contains match)
        _filePatterns =
        [
            "reference.cs", "temporarygeneratedfile", "assemblyinfo", "jquery", "bootstrap"
        ];

        // Known third-party bundle file prefixes that are commonly committed into repos.
        _bundledAssetPrefixes =
        [
            "socket.io",
            "socket_io",
            "socket-io",
            "socket.io-client",
            "socket_io_client",
            "socket-io-client"
        ];

        // Directory patterns to ignore. Matched as a whole path SEGMENT (see ShouldIgnoreDirectory),
        // so "packages/" cannot be tripped by a folder called "mypackages".
        _directoryPatterns =
        [
            // Build output / IDE
            "bin/", "obj/", "debug/", "release/", ".vs/", ".vscode/", ".idea/", ".git/",

            // JavaScript
            "node_modules/", "bower_components/", "jspm_packages/", "typings/", "wwwroot/lib/",

            // Generic vendoring conventions
            "vendor/", "vendors/", "third_party/", "third-party/", "thirdparty/",
            "3rdparty/", "3rd_party/", "packages/", "external/", "externals/",
            "extern/", "deps/", "dependencies/", "submodules/",

            // Unreal Engine build output and caches, at a project's root or a plugin's
            // (Plugins/X/Intermediate/...), so matched at any depth. Intermediate/ is where the
            // generated .gen.cpp files live. ThirdParty/ is already covered by "thirdparty/".
            // ponytail: a hand-written folder named Intermediate or Binaries would be skipped too;
            // narrow to "beside a .uproject/.uplugin" if that ever happens.
            "intermediate/", "binaries/", "deriveddatacache/",

            // Python. A checked-in virtualenv or site-packages tree is somebody else's code by
            // definition, and both are commonly committed by accident.
            "site-packages/", "dist-packages/", ".venv/", "venv/", "virtualenv/",
            "__pycache__/", ".tox/", ".eggs/", "eggs/", "*.egg-info/",

            // Unity. `Assets/Plugins/` is Unity's documented home for third-party plugins and
            // `Assets/StandardAssets` ships with the editor, so both are unambiguous wherever they
            // appear. Unity's generated caches (Library/, Temp/, Logs/, Builds/) are NOT here —
            // those names are too generic to blanket-match, and live in _rootOnlyDirectoryNames.
            "assets/plugins/", "assets/thirdparty/", "assets/third-party/",
            "assets/standard assets/", "assets/standardassets/",

            // Ruby / Rust
            ".bundle/", "vendor/bundle/", "target/debug/", "target/release/",
        ];

        // Matched only at the repository ROOT. These are real directory names a project might
        // legitimately use deeper in its own tree — `src/Library/` could easily be hand-written
        // code — but at the top level of a Unity project they are the editor's generated caches,
        // excluded by Unity's own .gitignore, and only present when committed by mistake.
        // Restricting them to depth 0 gets the common case without risking a folder of real source.
        _rootOnlyDirectoryNames =
        [
            "library", "temp", "logs", "builds", "build", "obj", "dist",
            "usersettings", "memorycaptures", "recordings",
            // Unreal's Saved/ (logs, autosaves, local config): a real source folder could be
            // called that deeper in a tree, so only at the root.
            "saved"
        ];
    }

    /// <summary>
    /// Determines whether a file should be ignored during line counting.
    /// </summary>
    /// <param name="fileName">The name of the file (e.g., "Program.cs").</param>
    /// <param name="filePath">The full path to the file within the repository.</param>
    /// <summary>
    /// Custom glob patterns to ignore during repository analysis (e.g. "**/*.generated.*", "migrations/**").
    /// </summary>
    public IReadOnlyList<string> CustomIgnoreGlobs { get; set; } = [];

    public static bool MatchesCustomGlobs(string relativePath, IEnumerable<string>? customGlobs)
    {
        if (customGlobs == null) return false;
        var globs = customGlobs.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
        if (globs.Count == 0) return false;

        var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var glob in globs)
        {
            matcher.AddInclude(glob.Trim());
        }

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return matcher.Match(normalized).HasMatches;
    }

    public bool ShouldIgnoreFile(string fileName, string filePath)
    {
        var nameLower = fileName.ToLowerInvariant();
        var normalizedPath = NormalizePath(filePath);

        // Custom user-defined globs
        if (CustomIgnoreGlobs.Count > 0 && MatchesCustomGlobs(string.IsNullOrEmpty(normalizedPath) ? nameLower : normalizedPath, CustomIgnoreGlobs))
        {
            _logger.LogDebug("Ignoring file (custom glob): {FilePath}", filePath);
            return true;
        }

        // Exact name match
        if (_exactFileNames.Contains(nameLower))
        {
            _logger.LogDebug("Ignoring file (exact name): {FileName}", fileName);
            return true;
        }

        // Extension suffix match
        if (_extensionSuffixes.Any(nameLower.EndsWith))
        {
            _logger.LogDebug("Ignoring file (extension): {FileName}", fileName);
            return true;
        }

        // Pattern match
        if (_filePatterns.Any(nameLower.Contains))
        {
            _logger.LogDebug("Ignoring file (pattern): {FileName}", fileName);
            return true;
        }

        if (IsBundledAssetFile(nameLower))
        {
            _logger.LogDebug("Ignoring bundled asset file: {FileName}", fileName);
            return true;
        }

        if (GeneratedPathFragments.Any(normalizedPath.Contains))
        {
            _logger.LogDebug("Ignoring generated asset file: {FilePath}", filePath);
            return true;
        }

        // Migration folder check
        if (normalizedPath.Contains(MigrationFolder, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Ignoring migration file: {FileName}", fileName);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Determines whether a directory should be ignored during repository traversal, judged by
    /// its path alone. Prefer the entry-aware overload wherever child names are available.
    /// </summary>
    public bool ShouldIgnoreDirectory(string directoryPath)
    {
        if (CustomIgnoreGlobs.Count > 0)
        {
            var dirNormalized = directoryPath.Replace('\\', '/').Trim('/');
            if (MatchesCustomGlobs(dirNormalized, CustomIgnoreGlobs) ||
                MatchesCustomGlobs(dirNormalized + "/_check_", CustomIgnoreGlobs))
            {
                _logger.LogDebug("Ignoring directory (custom glob): {DirectoryPath}", directoryPath);
                return true;
            }
        }

        var normalized = NormalizePath(directoryPath) + "/";

        var shouldIgnore = _directoryPatterns.Any(p =>
            normalized.EndsWith(p, StringComparison.Ordinal) || normalized.Contains("/" + p, StringComparison.Ordinal) || normalized.StartsWith(p, StringComparison.Ordinal));

        shouldIgnore = shouldIgnore || GeneratedPathFragments.Any(normalized.Contains);
        shouldIgnore = shouldIgnore || ReverseDomainPackageDirectory.IsMatch(normalized);
        shouldIgnore = shouldIgnore || IsRootOnlyIgnoredDirectory(normalized);

        if (shouldIgnore)
            _logger.LogDebug("Ignoring directory: {DirectoryPath}", directoryPath);

        return shouldIgnore;
    }

    /// <summary>
    /// Determines whether a directory should be ignored, given the names of its immediate
    /// children.
    /// </summary>
    /// <remarks>
    /// The path-only overload can only recognise vendored code whose *name* gives it away
    /// (<c>node_modules</c>, <c>vendor</c>, a reverse-domain package folder). It cannot recognise
    /// the common and much larger case: an entire third-party repository copied into a folder the
    /// author named themselves. Those are identified by what they CONTAIN — a licence, a
    /// <c>.github</c> directory, a <c>.gitmodules</c> file — which needs the child names.
    /// </remarks>
    public bool ShouldIgnoreDirectory(string directoryPath, IEnumerable<string> entryNames)
    {
        if (ShouldIgnoreDirectory(directoryPath)) return true;

        // Only ever true below the repository root: the root is SUPPOSED to carry a licence and a
        // .github folder. It is a nested copy of one that means vendored code.
        if (!IsEmbeddedRepositoryRoot(directoryPath, entryNames)) return false;

        _logger.LogInformation(
            "Ignoring vendored directory {DirectoryPath} — it carries repository-root marker files " +
            "({Markers}), so it is a third-party project copied into this repository rather than " +
            "part of it.",
            directoryPath,
            string.Join(", ", MatchedRootMarkers(entryNames)));

        return true;
    }

    /// <summary>
    /// True when a NON-ROOT directory looks like the root of some other project — see
    /// <see cref="RepositoryRootMarkers"/>.
    /// </summary>
    private static bool IsEmbeddedRepositoryRoot(string directoryPath, IEnumerable<string> entryNames)
    {
        // Depth 0 is this repository's own root. Everything it contains is, by definition, this
        // repository — so the marker files there are its own and mean nothing.
        var normalized = NormalizePath(directoryPath).Trim('/');
        if (normalized.Length == 0) return false;

        return MatchedRootMarkers(entryNames).Count >= RepositoryRootMarkerThreshold;
    }

    private static List<string> MatchedRootMarkers(IEnumerable<string> entryNames) =>
        entryNames
            .Where(n => RepositoryRootMarkers.Contains(n.Trim()))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Matches <see cref="_rootOnlyDirectoryNames"/>, but only for a top-level directory —
    /// `Library/` at the root of a Unity project is a generated cache, `src/Library/` is code.
    /// </summary>
    private bool IsRootOnlyIgnoredDirectory(string normalizedWithTrailingSlash)
    {
        var trimmed = normalizedWithTrailingSlash.Trim('/');
        return !trimmed.Contains('/') && _rootOnlyDirectoryNames.Contains(trimmed);
    }

    private static string NormalizePath(string path) => path.Replace("\\", "/").ToLowerInvariant();

    private bool IsBundledAssetFile(string normalizedFileName)
    {
        var extension = Path.GetExtension(normalizedFileName);
        if (extension is not ".js" and not ".css")
        {
            return false;
        }

        return _bundledAssetPrefixes.Any(normalizedFileName.StartsWith);
    }
}
