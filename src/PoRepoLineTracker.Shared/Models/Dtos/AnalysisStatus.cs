namespace PoRepoLineTracker.Shared.Models.Dtos;

/// <summary>
/// The grid's Status column values. One list, because the column computes them and the status
/// cell renders them — two copies could drift and a value the cell never matches would show nothing.
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
