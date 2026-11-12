using Azure;
using Azure.Data.Tables;

namespace PoRepoLineTracker.Api.Storage;

/// <summary>
/// Azure Table Storage entity for User.
/// PartitionKey: "USER" (single partition for all users)
/// RowKey: GitHubId (unique identifier from GitHub)
/// </summary>
public class UserEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "USER";
    public string RowKey { get; set; } = default!; // GitHubId
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public Guid Id { get; set; }
    public string GitHubId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string AvatarUrl { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty; // Encrypted by UserService (IDataProtector)
    public DateTime CreatedAt { get; set; }
    public DateTime LastLoginAt { get; set; }
    public DateTime? TokenExpiresAt { get; set; }

    public User ToDomainModel()
    {
        return new User
        {
            Id = new UserId(Id),
            GitHubId = GitHubId,
            Username = Username,
            DisplayName = DisplayName,
            Email = Email,
            AvatarUrl = AvatarUrl,
            AccessToken = AccessToken,
            CreatedAt = CreatedAt,
            LastLoginAt = LastLoginAt,
            TokenExpiresAt = TokenExpiresAt
        };
    }

    public static UserEntity FromDomainModel(User model)
    {
        return new UserEntity
        {
            PartitionKey = "USER",
            RowKey = model.GitHubId,
            Id = model.Id.Value,
            GitHubId = model.GitHubId,
            Username = model.Username,
            DisplayName = model.DisplayName,
            Email = model.Email,
            AvatarUrl = model.AvatarUrl,
            AccessToken = model.AccessToken,
            CreatedAt = model.CreatedAt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(model.CreatedAt, DateTimeKind.Utc)
                : model.CreatedAt.ToUniversalTime(),
            LastLoginAt = model.LastLoginAt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(model.LastLoginAt, DateTimeKind.Utc)
                : model.LastLoginAt.ToUniversalTime(),
            TokenExpiresAt = model.TokenExpiresAt.HasValue && model.TokenExpiresAt.Value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(model.TokenExpiresAt.Value, DateTimeKind.Utc)
                : model.TokenExpiresAt?.ToUniversalTime()
        };
    }
}
