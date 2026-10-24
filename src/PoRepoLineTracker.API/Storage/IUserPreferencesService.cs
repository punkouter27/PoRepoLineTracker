
namespace PoRepoLineTracker.API.Storage;

/// <summary>
/// Service for managing user preferences.
/// </summary>
public interface IUserPreferencesService
{
    /// <summary>
    /// Gets preferences for a user. Returns default preferences if none exist.
    /// </summary>
    Task<UserPreferences> GetPreferencesAsync(UserId userId);

    /// <summary>
    /// Saves user preferences.
    /// </summary>
    Task SavePreferencesAsync(UserPreferences preferences);

    /// <summary>
    /// Gets file extensions for a specific user.
    /// </summary>
    Task<List<string>> GetFileExtensionsAsync(UserId userId);

    /// <summary>Deletes the user's stored preferences. No-op when there are none.</summary>
    Task DeletePreferencesAsync(UserId userId);
}
