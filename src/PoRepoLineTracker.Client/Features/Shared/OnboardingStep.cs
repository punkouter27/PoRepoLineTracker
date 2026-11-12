namespace PoRepoLineTracker.Client.Features.Shared;

/// <summary>
/// A single labelled step in <see cref="OnboardingEmptyState"/>.
/// </summary>
public record OnboardingStep(string Icon, string Title, string Caption)
{
    /// <summary>The three steps the repositories page shows by default.</summary>
    public static readonly IReadOnlyList<OnboardingStep> RepositoriesDefault = new OnboardingStep[]
    {
        new("add_link",       "Add Repo",       "Connect your GitHub repository"),
        new("manage_search",  "Auto-Analyze",   "Commits scanned in background"),
        new("trending_up",    "Track Trends",   "Charts, extensions & top files"),
    };
}
