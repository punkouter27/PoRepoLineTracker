namespace PoRepoLineTracker.Shared.Models.Dtos;

/// <summary>The outcome of <c>DELETE /api/account</c>. The data is gone whenever this is returned.</summary>
public sealed class AccountDeletedDto
{
    /// <summary>
    /// Whether GitHub confirmed the app's OAuth grant was revoked. False when OAuth is not
    /// configured or GitHub could not be reached; the user is then pointed at GitHub's settings.
    /// </summary>
    public bool GrantRevoked { get; set; }
}

/// <summary>Body and reply of <c>PUT /api/repositories/{id}/visibility</c>.</summary>
public sealed class RepositoryVisibilityDto
{
    /// <summary>Whether the anonymous badge and share page answer for this repository.</summary>
    public bool IsPublic { get; set; }
}
