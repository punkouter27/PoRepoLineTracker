using PoRepoLineTracker.Api.Storage;
using Serilog;

namespace PoRepoLineTracker.Api.Auth;

/// <summary>
/// The one guard for "does this caller own the repository they named".
///
/// <para><b>Why it lives in Auth and not in a slice.</b> Every route that takes a repository id
/// from the URL needs this check, and repository ids appear in more than one slice — which are
/// forbidden from referencing each other. Left as a private helper on one slice's endpoint class,
/// the second slice to need it would have had to copy it, and a copied authorization check is the
/// kind of duplication that goes wrong quietly: the copy stops matching, and the failure mode is a
/// route that returns another user's data rather than a broken build.</para>
///
/// <para>Authorization is a cross-cutting concern, not slice logic, so it sits beside the other
/// cross-cutting auth code.</para>
/// </summary>
public static class RepositoryOwnership
{
    /// <summary>
    /// Loads the repository and confirms the caller owns it.
    ///
    /// <para>Returns the repository on success and an <see cref="IResult"/> to return otherwise —
    /// 404 when it does not exist, 403 when it belongs to someone else. The distinction is
    /// deliberate: a caller who owns nothing learns only that the id is not theirs, and the
    /// attempt is logged.</para>
    /// </summary>
    /// <param name="action">Verb phrase for the log line, e.g. "read linehistory for".</param>
    public static async Task<(GitHubRepository? Repository, IResult? Error)> AuthorizeAsync(
        IRepositoryDataService repoDataService, RepositoryId repositoryId, UserId userId, string action)
    {
        var existing = await repoDataService.GetRepositoryByIdAsync(repositoryId);
        if (existing == null)
            return (null, Results.NotFound($"Repository {repositoryId} not found."));

        if (existing.UserId != userId)
        {
            Log.Warning("IDOR attempt: user {UserId} tried to {Action} repo {RepositoryId} owned by {OwnerId}",
                userId, action, repositoryId, existing.UserId);
            return (null, Results.Forbid());
        }

        return (existing, null);
    }
}
