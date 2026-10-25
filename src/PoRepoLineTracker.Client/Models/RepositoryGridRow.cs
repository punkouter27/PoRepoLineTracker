using PoRepoLineTracker.Shared.Domain;
using PoRepoLineTracker.Shared.Models.Dtos;

namespace PoRepoLineTracker.Client.Models;

/// <summary>
/// Row shape for the <c>RepositoriesGrid</c> Radzen column templates. The column-bound
/// properties must keep their names — the columns sort and filter on them by string, and the
/// saved sort in localStorage names them too, so a rename is a wire-shape change at the grid level.
/// </summary>
/// <param name="Movement">The repository's 30-day figures from <c>/api/insights/portfolio</c>.
/// Null until that request lands, and for a repository it does not list.</param>
public sealed record RepositoryGridRow(
    GitHubRepository Repository,
    int? TotalLinesSortValue,
    RepositoryMovementDto? Movement = null)
{
    public RepositoryId Id => Repository?.Id ?? default;
    public string Owner => Repository?.Owner!;
    public string Name => Repository?.Name!;

    /// <summary>
    /// "owner/name" — the string the Repository column shows, and the one its filter matches on.
    /// The column sorts on <see cref="Name"/> but filters on this, so a search covers the owner
    /// half too.
    /// </summary>
    public string OwnerName => $"{Owner}/{Name}";

    public DateTime? LastAnalyzedCommitDate => Repository?.LastAnalyzedCommitDate;
    public int? NetChange30Days => Movement?.NetChange30Days;
    public int? Commits30Days => Movement?.Commits30Days;

    /// <summary>
    /// The stored outcome of the last run, as the Status column's sort and filter key. The live
    /// "analyzing" state is not part of it: that lives in the page's progress frames, and a
    /// filter that dropped a row the moment its job started would hide exactly the row being watched.
    /// </summary>
    public string Status =>
        AnalysisFailure.IsAuth(Repository?.LastAnalysisError) ? AnalysisStatus.Reauth
        : !string.IsNullOrEmpty(Repository?.LastAnalysisError) ? AnalysisStatus.Failed
        : LastAnalyzedCommitDate.HasValue ? AnalysisStatus.Done
        : Repository?.LastAnalysisAttemptUtc is not null ? AnalysisStatus.Empty
        : AnalysisStatus.Pending;
}
