using PoRepoLineTracker.Shared.Models.Dtos;
using PoRepoLineTracker.Shared.Domain;

namespace PoRepoLineTracker.Client.Models;

public class RepositoryPageState
{
    public string AddRepoMessage { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public bool IsLoadingRepositories { get; set; }
    public bool IsAddingRepository { get; set; }
    public bool IsLoadingGitHubRepos { get; set; }
    public string ProgressMessage { get; set; } = string.Empty;
    /// <summary>
    /// Progress percentage (0–100), or <c>null</c> when the work in flight cannot be measured as
    /// a fraction — e.g. a single round-trip POST whose server-side cost is one row per repo, but
    /// whose real wall-clock cost is dominated by the background analysis that follows. The
    /// modal renders <c>null</c> as an indeterminate <c>RadzenProgressBar</c>, which is what
    /// "we don't know yet" should look like rather than a bar that snaps from 0% to 100%.
    /// </summary>
    public int? ProgressPercentage { get; set; } = 0;
    public List<GitHubRepository> Repositories { get; set; } = new();
    public List<GitHubUserRepositoryDto> GitHubUserRepositories { get; set; } = new();

    public void ClearMessages()
    {
        AddRepoMessage = string.Empty;
        ErrorMessage = string.Empty;
        ProgressMessage = string.Empty;
        ProgressPercentage = 0;
    }

    public void SetProgress(string message, int? percentage)
    {
        ProgressMessage = message;
        ProgressPercentage = percentage;
    }
}
