namespace PoRepoLineTracker.Client.Components.Shared;

/// <summary>
/// Icon + copy hints for <see cref="EmptyState"/>. Centralised here so every empty state in the
/// app draws the same picture for the same situation (no more <c>bar_chart</c> here, <c>insights</c>
/// there, <c>cloud</c> somewhere else for "no data").
/// </summary>
public enum EmptyStateKind
{
    /// <summary>Generic data panel with nothing to render. Default icon <c>bar_chart</c>.</summary>
    NoData,
    /// <summary>Insights / analytics page awaiting data. Icon <c>insights</c>.</summary>
    NoInsights,
    /// <summary>Target resource (typically a single repository) could not be found. Icon <c>search_off</c>.</summary>
    NotFound,
    /// <summary>
    /// First run: nothing is set up yet. The one kind with a second presentation — a three-step
    /// "connect → analyse → track" panel and one call to action, because "there is nothing here"
    /// is not a useful thing to say to someone who has not started.
    /// </summary>
    Onboarding,
}