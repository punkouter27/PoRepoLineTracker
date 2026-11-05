using PoRepoLineTracker.Shared.Domain;

namespace PoRepoLineTracker.Shared.Models.Dtos;

/// <summary>
/// Returned by the bulk-add endpoint so the caller can show distinct banners
/// for newly added vs. already-tracked repositories.
/// </summary>
public class BulkAddResult
{
    public List<GitHubRepository> Added { get; init; } = [];
    public List<GitHubRepository> AlreadyTracked { get; init; } = [];
}
