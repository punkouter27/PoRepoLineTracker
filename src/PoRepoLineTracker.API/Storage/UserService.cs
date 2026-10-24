using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PoRepoLineTracker.API.Storage;

/// <summary>
/// Azure Table Storage implementation of IUserService.
/// </summary>
public class UserService : IUserService
{
    private readonly TableClient _userTableClient;
    private readonly ILogger<UserService> _logger;
    private bool _tableInitialized = false;

    /// <summary>
    /// Encrypts the GitHub access token at rest. It is a live `repo`-scope credential — read
    /// access to every private repository the user has — and was stored in plaintext, so anyone
    /// who could read the Users table (a leaked storage key, a support export) held all of them.
    /// </summary>
    private readonly IDataProtector _tokenProtector;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public UserService(TableServiceClient tableServiceClient, IConfiguration configuration, IHostEnvironment environment, IDataProtectionProvider dataProtection, ILogger<UserService> logger)
    {
        _logger = logger;
        _configuration = configuration;
        _environment = environment;
        _tokenProtector = dataProtection.CreateProtector("PoRepoLineTracker.GitHubAccessToken");
        var tableName = configuration[ConfigKeys.AzureTableStorage.UserTableName] ?? "PoRepoLineTrackerUsers";
        _userTableClient = tableServiceClient.GetTableClient(tableName);
    }

    private async Task EnsureTableExistsAsync()
    {
        if (!_tableInitialized)
        {
            try
            {
                await _userTableClient.CreateIfNotExistsAsync();
                _tableInitialized = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ensuring user table exists: {ErrorMessage}", ex.Message);
                throw;
            }
        }
    }

    public async Task<User?> GetUserByIdAsync(UserId userId)
    {
        await EnsureTableExistsAsync();

        try
        {
            // Query by Id property since RowKey is GitHubId
            var query = _userTableClient.QueryAsync<UserEntity>(e => e.Id == userId.Value);
            await foreach (var entity in query)
            {
                return ToUser(entity);
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user by ID {UserId}: {ErrorMessage}", userId, ex.Message);
            throw;
        }
    }

    public async Task<string?> ResolveGitHubTokenAsync(UserId userId) => ChooseGitHubToken(
        // UserId.Empty: a legacy row with no owner — there is no one to look up.
        userId == UserId.Empty ? null : (await GetUserByIdAsync(userId))?.AccessToken,
        _environment, _configuration[ConfigKeys.GitHub.Pat]);

    /// <summary>
    /// The user's own token wins. Anywhere fake auth does not exist the PAT fallback is a confused
    /// deputy: any principal whose row lacks a token would act with the server's credential (list,
    /// and clone, the PAT owner's private repositories). Split out so the rule is testable
    /// without storage.
    /// </summary>
    internal static string? ChooseGitHubToken(string? userToken, IHostEnvironment environment, string? serverPat) =>
        !string.IsNullOrEmpty(userToken) ? userToken
        : FakeAuthHandler.IsAllowedIn(environment) ? serverPat
        : null;

    private async Task<User?> GetUserByGitHubIdAsync(string gitHubId)
    {
        await EnsureTableExistsAsync();

        try
        {
            var response = await _userTableClient.GetEntityIfExistsAsync<UserEntity>("USER", gitHubId);
            if (response.HasValue && response.Value is not null)
            {
                return ToUser(response.Value);
            }
            return null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user by GitHub ID {GitHubId}: {ErrorMessage}", gitHubId, ex.Message);
            throw;
        }
    }

    public async Task<User> UpsertUserAsync(User user)
    {
        await EnsureTableExistsAsync();

        try
        {
            var existingUser = await GetUserByGitHubIdAsync(user.GitHubId);
            if (existingUser != null)
            {
                // Update existing user
                user.Id = existingUser.Id;
                user.CreatedAt = existingUser.CreatedAt;
                user.LastLoginAt = DateTime.UtcNow;
            }
            else
            {
                // New user
                user.Id = UserId.New();
                user.CreatedAt = DateTime.UtcNow;
                user.LastLoginAt = DateTime.UtcNow;
            }

            var entity = UserEntity.FromDomainModel(user);
            if (!string.IsNullOrEmpty(entity.AccessToken))
                entity.AccessToken = _tokenProtector.Protect(entity.AccessToken);
            await _userTableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);

            _logger.LogInformation("Upserted user {Username} (GitHub ID: {GitHubId})", user.Username, user.GitHubId);
            return user;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error upserting user {Username}: {ErrorMessage}", user.Username, ex.Message);
            throw;
        }
    }

    public async Task DeleteUserAsync(UserId userId)
    {
        await EnsureTableExistsAsync();

        // By Id, like GetUserByIdAsync: the RowKey is the GitHub id, which the caller does not hold.
        await foreach (var entity in _userTableClient.QueryAsync<UserEntity>(e => e.Id == userId.Value))
        {
            await _userTableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, ETag.All);
        }
        _logger.LogInformation("Deleted user {UserId}", userId);
    }

    private User ToUser(UserEntity entity)
    {
        var user = entity.ToDomainModel();
        if (string.IsNullOrEmpty(user.AccessToken)) return user;

        try
        {
            user.AccessToken = _tokenProtector.Unprotect(user.AccessToken);
        }
        catch (CryptographicException)
        {
            // A row written before tokens were encrypted (or under a rotated-away key). GitHub
            // tokens are recognisable, so a legacy plaintext one is used as-is and re-encrypted
            // at the user's next sign-in; anything else is unusable and treated as "no token".
            if (!user.AccessToken.StartsWith("gh", StringComparison.Ordinal))
                user.AccessToken = string.Empty;
        }

        return user;
    }
}
