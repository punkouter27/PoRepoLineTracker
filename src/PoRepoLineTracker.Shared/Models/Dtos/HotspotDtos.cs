namespace PoRepoLineTracker.Shared.Models.Dtos;

/// <summary>
/// Files that are both large and often changed over a window: the "code as a crime scene"
/// hotspot view. Size is code lines at the newest analysed commit; change is how many commits
/// touched the file inside the window.
/// </summary>
public sealed class HotspotsDto
{
    /// <summary>False when the server has no clone to read (a recycled host); re-analysing restores it.</summary>
    public bool HasClone { get; set; }

    public int Days { get; set; }

    /// <summary>The commit the sizes were measured at (full SHA, for links to GitHub).</summary>
    public string CommitSha { get; set; } = string.Empty;

    /// <summary>How many counted files changed in the window; <see cref="Files"/> is capped below it.</summary>
    public int FilesChanged { get; set; }

    /// <summary>Highest hotspot score first.</summary>
    public List<HotspotFileDto> Files { get; set; } = [];
}

public sealed class HotspotFileDto
{
    /// <summary>Repository-relative, forward-slashed.</summary>
    public string Path { get; set; } = string.Empty;

    public int CodeLines { get; set; }

    /// <summary>The same cyclomatic approximation Code Health reports. A proxy, not a parser (SPEC §10).</summary>
    public int Complexity { get; set; }

    /// <summary>Commits in the window that touched this file.</summary>
    public int Commits { get; set; }

    /// <summary>Lines added plus removed by those commits.</summary>
    public int LinesChanged { get; set; }
}
