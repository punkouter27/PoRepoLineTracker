
namespace PoRepoLineTracker.Api.Storage;

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

    /// <summary>
    /// Adds <paramref name="added"/> to the user's saved list once per <paramref name="version"/>,
    /// creating the row (from the defaults) if there is none. False when already at that version.
    /// </summary>
    Task<bool> UpgradeDefaultsAsync(UserId userId, int version, IReadOnlyList<string> added);
}
