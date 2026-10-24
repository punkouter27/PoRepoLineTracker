namespace PoRepoLineTracker.API.Analysis;

/// <summary>
/// One source file's text at a particular commit, read straight from the git object store.
/// </summary>
/// <param name="Path">Repository-relative path, forward-slashed, as git stores it.</param>
/// <param name="Extension">Lower-cased extension including the dot, matching the counted-extension keys.</param>
/// <param name="Content">The file's full text.</param>
public sealed record SourceFile(string Path, string Extension, string Content);

/// <summary>How often a file changed in a window: commits that touched it, and lines added plus removed.</summary>
public sealed record FileChurn(int Commits, int LinesChanged);
