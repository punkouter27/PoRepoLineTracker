namespace PoRepoLineTracker.Shared.Models.Dtos;

/// <summary>Whether the optional Claude features are configured on this deployment.</summary>
public sealed class AiStatusDto
{
    public bool Available { get; set; }
}

/// <summary>A short prose reading of the digest window. Null text means "nothing to add".</summary>
public sealed class DigestNarrativeDto
{
    public string? Text { get; set; }
}

/// <summary>A question about the portfolio in the user's own words.</summary>
public sealed class GridQueryRequest
{
    public string Query { get; set; } = string.Empty;
}

/// <summary>
/// What a natural-language question maps to on the repositories grid. Every field is either
/// free text the grid already searches by, or one of a fixed set of values the server validates —
/// the model chooses among existing filters, it does not get to invent behaviour.
/// </summary>
public sealed class GridFilterDto
{
    /// <summary>Text to put in the name search box; empty for none.</summary>
    public string Search { get; set; } = string.Empty;

    /// <summary>One of <see cref="AnalysisStatus.All"/>, or empty for any.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>"Name", "TotalLines", "NetChange30Days", "Commits30Days", "LastCommit", or empty to leave the sort alone.</summary>
    public string SortBy { get; set; } = string.Empty;

    public bool Descending { get; set; }
}

/// <summary>
/// The grid's Status column values. One list, because the column computes them, the assistant's
/// schema offers them and the client re-checks the reply against them — three copies could drift
/// and a reply the column never produces would filter the grid to nothing.
/// </summary>
public static class AnalysisStatus
{
    public const string Done = "Done";
    public const string Failed = "Failed";
    public const string Empty = "Empty";
    public const string Pending = "Pending";

    /// <summary>Failed because GitHub refused the stored token; signing in again fixes it.</summary>
    public const string Reauth = "Reauth";

    public static readonly string[] All = [Done, Failed, Empty, Pending, Reauth];
}
