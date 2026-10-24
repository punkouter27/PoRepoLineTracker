
namespace PoRepoLineTracker.API.Storage;

/// <summary>
/// Service for managing user accounts and authentication state.
/// </summary>
public interface IUserService
{
    /// <summary>
    /// Gets a user by their internal ID.
    /// </summary>
    Task<User?> GetUserByIdAsync(UserId userId);

    /// <summary>
    /// Creates or updates a user based on GitHub OAuth data.
    /// If user exists, updates their token and last login time.
    /// </summary>
    Task<User> UpsertUserAsync(User user);

    /// <summary>
    /// The GitHub token to act for this user with: their own OAuth token, else — only where
    /// <see cref="FakeAuthHandler.IsAllowedIn"/> (Development/Test, whose users have no token) —
    /// the server-configured <c>GitHub:PAT</c>. Null when neither applies; a private clone or
    /// listing then fails and is reported as such.
    /// </summary>
    Task<string?> ResolveGitHubTokenAsync(UserId userId);

    /// <summary>Deletes the user's row, encrypted token included. No-op when there is none.</summary>
    Task DeleteUserAsync(UserId userId);
}
