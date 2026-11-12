using System.Security.Claims;

namespace PoRepoLineTracker.Api.Extensions;

/// <summary>
/// Reads the caller's identity off the authenticated principal.
///
/// The claim name and the parse were previously repeated at every endpoint, so the
/// literal "UserId" appeared ~20 times and each site re-derived a raw <see cref="Guid"/>. One
/// helper means one spelling of the claim and a <see cref="UserId"/> that cannot be transposed
/// with a <see cref="RepositoryId"/> at the call site.
/// </summary>
internal static class ClaimsPrincipalExtensions
{
    /// <summary>The claim carrying our internal user id, set during the OAuth ticket callback.</summary>
    internal const string UserIdClaim = "UserId";

    /// <summary>
    /// Returns false when the principal is anonymous or the claim is missing/malformed — the
    /// caller should answer 401 in that case.
    /// </summary>
    internal static bool TryGetUserId(this ClaimsPrincipal principal, out UserId userId)
    {
        var claim = principal.FindFirst(UserIdClaim)?.Value;
        return UserId.TryParse(claim, out userId);
    }

    /// <summary>The raw claim, or "anonymous" — a partition/log key, not an identity check.</summary>
    internal static string UserKey(this ClaimsPrincipal principal) =>
        principal.FindFirst(UserIdClaim)?.Value ?? "anonymous";
}
