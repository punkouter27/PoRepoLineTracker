using Azure;
using Azure.Data.Tables;

namespace PoRepoLineTracker.Api.Storage;

/// <summary>
/// Azure Table Storage entity for user preferences.
/// PartitionKey: "PREFS"
/// RowKey: UserId
/// </summary>
public class UserPreferencesEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "PREFS";
    public string RowKey { get; set; } = string.Empty; // UserId
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    /// <summary>
    /// User ID (same as RowKey for querying).
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Comma-separated list of file extensions.
    /// </summary>
    public string FileExtensions { get; set; } = string.Empty;

    /// <summary>
    /// Semicolon-separated list of custom glob ignore patterns.
    /// </summary>
    public string CustomIgnoreGlobs { get; set; } = string.Empty;

    /// <summary>
    /// When preferences were last updated.
    /// </summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// The last defaults upgrade applied to this list (<c>DefaultsUpgrade</c>); 0 on older rows.
    /// Server-only: the client never sees it, and a save keeps the stored value.
    /// </summary>
    public int DefaultsVersion { get; set; }

    public UserPreferencesEntity() { }

    public UserPreferencesEntity(PoRepoLineTracker.Shared.Domain.UserPreferences prefs)
    {
        PartitionKey = "PREFS";
        RowKey = prefs.UserId.ToString();
        UserId = prefs.UserId.Value;
        FileExtensions = string.Join(",", prefs.FileExtensions);
        CustomIgnoreGlobs = string.Join(";", prefs.CustomIgnoreGlobs ?? []);
        LastUpdated = prefs.LastUpdated;
    }

    public PoRepoLineTracker.Shared.Domain.UserPreferences ToDomainModel()
    {
        return new PoRepoLineTracker.Shared.Domain.UserPreferences
        {
            UserId = new PoRepoLineTracker.Shared.Domain.UserId(UserId),
            FileExtensions = string.IsNullOrEmpty(FileExtensions)
                ? PoRepoLineTracker.Shared.Domain.UserPreferences.DefaultFileExtensions
                : FileExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
            CustomIgnoreGlobs = string.IsNullOrEmpty(CustomIgnoreGlobs)
                ? []
                : CustomIgnoreGlobs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            LastUpdated = LastUpdated
        };
    }
}
