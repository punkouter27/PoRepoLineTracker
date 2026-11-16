
namespace PoRepoLineTracker.Api.Analysis;

/// <summary>
/// Tracks live analysis progress for repository jobs running in background tasks.
/// Implemented as a singleton so background Task.Run jobs share state with the API request threads.
/// </summary>
public interface IAnalysisProgressService
{
    /// <summary>
    /// Opens a job and records who owns it.
    ///
    /// <para>Every other method on this interface identifies a job by repository id alone, which
    /// was enough while progress was only ever read back by a caller that had already proved it
    /// owned the repository. Pushing progress inverts that: the service now has to decide who to
    /// send an update to, and it cannot ask — the reports arrive from a detached background task
    /// with no request, no principal and no scoped services. So ownership is recorded once here,
    /// at the point the job starts and the repository is in hand.</para>
    ///
    /// <para>Calling it also clears any state left by a previous run of the same repository, so a
    /// re-analysis does not inherit the last run's error message or commit counts.</para>
    /// </summary>
    void BeginJob(RepositoryId repositoryId, UserId userId, string owner, string name);

    /// <summary>Report that a job has reached a new step.</summary>
    void ReportStep(RepositoryId repositoryId, int stepIndex, string stepName, string stepDescription);

    /// <summary>Report how many commits have been found (before processing starts).</summary>
    void ReportCommitsFound(RepositoryId repositoryId, int total);

    /// <summary>
    /// Report progress within the commit processing loop.
    ///
    /// <para>The two tally arguments are optional so a caller that only knows the counts stays
    /// valid. They exist for the live view: a repository with 4,000 commits spends minutes on
    /// this one step, and a bare percentage that creeps a point every few seconds is
    /// indistinguishable from a hang. A running line tally and the set of file types being
    /// discovered give it something that visibly moves.</para>
    /// </summary>
    /// <param name="linesCounted">Running total of lines counted so far in this run. Churn, not repository size.</param>
    /// <param name="extensions">File types discovered so far, most lines first. Pass a fresh list; it is published as-is.</param>
    void ReportCommitProgress(
        RepositoryId repositoryId,
        int processed,
        int total,
        long linesCounted = 0,
        List<string>? extensions = null);

    /// <summary>Mark a job as finished (success).</summary>
    void ReportComplete(RepositoryId repositoryId);

    /// <summary>Mark a job as failed with an error message.</summary>
    void ReportError(RepositoryId repositoryId, string errorMessage);

    /// <summary>Get the current progress snapshot for a repository. Returns null if no job is tracked.</summary>
    AnalysisProgressDto? GetProgress(RepositoryId repositoryId);
}
