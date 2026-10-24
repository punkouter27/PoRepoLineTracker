using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PoRepoLineTracker.API.Storage;

/// <summary>
/// Azure Table Storage implementation of user preferences service.
/// </summary>
public class UserPreferencesService : IUserPreferencesService
{
    private readonly TableClient _preferencesTableClient;
    private readonly ILogger<UserPreferencesService> _logger;
    private bool _tableInitialized;

    public UserPreferencesService(TableServiceClient tableServiceClient, IConfiguration configuration, ILogger<UserPreferencesService> logger)
    {
        var tableName = configuration[ConfigKeys.AzureTableStorage.UserPreferencesTableName] ?? "PoRepoLineTrackerUserPreferences";
        _preferencesTableClient = tableServiceClient.GetTableClient(tableName);
        _logger = logger;
    }

    private async Task EnsureTableExistsAsync()
    {
        if (!_tableInitialized)
        {
            await _preferencesTableClient.CreateIfNotExistsAsync();
            _tableInitialized = true;
        }
    }

    public async Task<UserPreferences> GetPreferencesAsync(UserId userId)
    {
        await EnsureTableExistsAsync();

        try
        {
            var response = await _preferencesTableClient.GetEntityIfExistsAsync<UserPreferencesEntity>("PREFS", userId.ToString());
            if (response.HasValue && response.Value is not null)
            {
                return response.Value.ToDomainModel();
            }
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Entity not found, return defaults
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting preferences for user {UserId}", userId);
        }

        // Return default preferences
        return new UserPreferences
        {
            UserId = userId,
            FileExtensions = UserPreferences.DefaultFileExtensions,
            LastUpdated = DateTime.UtcNow
        };
    }

    public async Task SavePreferencesAsync(UserPreferences preferences)
    {
        await EnsureTableExistsAsync();

        try
        {
            // Create entity with updated timestamp (preferences is a record so we use 'with')
            var updatedPrefs = preferences with { LastUpdated = DateTime.UtcNow };
            var entity = new UserPreferencesEntity(updatedPrefs);
            await _preferencesTableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            _logger.LogInformation(
                "Saved preferences for user {UserId} with {Count} extensions",
                updatedPrefs.UserId,
                updatedPrefs.FileExtensions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving preferences for user {UserId}", preferences.UserId);
            throw;
        }
    }

    public async Task DeletePreferencesAsync(UserId userId)
    {
        await EnsureTableExistsAsync();
        try
        {
            await _preferencesTableClient.DeleteEntityAsync("PREFS", userId.ToString(), Azure.ETag.All);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Never saved preferences — nothing to delete.
        }
    }

    public async Task<List<string>> GetFileExtensionsAsync(UserId userId)
    {
        var prefs = await GetPreferencesAsync(userId);
        return prefs.FileExtensions;
    }
}
