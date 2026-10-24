using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace PoRepoLineTracker.API.Features.Repositories;

/// <summary>
/// Deletes everything the app holds for one user, then revokes the app's GitHub grant.
///
/// <para><b>Why this lives in the Repositories slice.</b> Almost all of a user's data is their
/// repositories: rows, commit history, code-health snapshots and clones on disk. That removal
/// already exists here (<see cref="RemoveAllRepositoriesCommandHandler"/>), and so does the
/// queue check that must refuse while a job runs. Slices may not reference each other, so a
/// separate Account slice would have had to copy both.</para>
/// </summary>
public sealed class DeleteAccountCommandHandler(
    RemoveAllRepositoriesCommandHandler removeAllHandler,
    IUserService userService,
    IUserPreferencesService preferencesService,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILogger<DeleteAccountCommandHandler> logger)
{
    /// <returns>Whether GitHub confirmed the grant revocation. False is not a failure of the
    /// delete: the data is gone either way, and the user can revoke from GitHub's settings.</returns>
    public async Task<bool> Handle(UserId userId, CancellationToken cancellationToken = default)
    {
        // Read before the row is deleted: the token is needed for the revocation below, and the
        // Users row is the only place it is kept.
        var user = await userService.GetUserByIdAsync(userId);

        await removeAllHandler.Handle(new RemoveAllRepositoriesCommand(userId), cancellationToken);
        await preferencesService.DeletePreferencesAsync(userId);
        await userService.DeleteUserAsync(userId);
        logger.LogInformation("Deleted account {UserId}", userId);

        return !string.IsNullOrEmpty(user?.AccessToken) && await RevokeGrantAsync(user.AccessToken, cancellationToken);
    }

    /// <summary>
    /// <c>DELETE /applications/{client_id}/grant</c> removes the OAuth authorization itself, not
    /// just this token, so the app vanishes from the user's GitHub "Authorized OAuth Apps" list.
    /// Authenticated as the APP (client id and secret), which is why it needs the secret and
    /// cannot run where OAuth is not configured.
    /// </summary>
    private async Task<bool> RevokeGrantAsync(string accessToken, CancellationToken cancellationToken)
    {
        // Same choice of credentials /auth/login makes, so the grant revoked is the one that was granted.
        var clientId = environment.IsDevelopment()
            ? configuration[ConfigKeys.GitHub.DevClientId] ?? configuration[ConfigKeys.GitHub.ClientId]
            : configuration[ConfigKeys.GitHub.ClientId];
        var clientSecret = environment.IsDevelopment()
            ? configuration[ConfigKeys.GitHub.DevClientSecret] ?? configuration[ConfigKeys.GitHub.ClientSecret]
            : configuration[ConfigKeys.GitHub.ClientSecret];

        // The dev sign-in stores the server PAT as the user's token. It is not this app's grant,
        // and sending the server's credential anywhere it is not needed is the wrong habit.
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret)
            || accessToken == configuration[ConfigKeys.GitHub.Pat])
            return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"applications/{Uri.EscapeDataString(clientId)}/grant")
            {
                Content = JsonContent.Create(new Dictionary<string, string> { ["access_token"] = accessToken })
            };
            // Set on the request, so it replaces the client's default PAT header rather than joining it.
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));

            using var response = await httpClientFactory.CreateClient("GitHubClient").SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return true;

            logger.LogWarning("GitHub refused the grant revocation: {Status}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reach GitHub to revoke the OAuth grant");
            return false;
        }
    }
}

internal static class AccountEndpoints
{
    internal static void MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDelete("/api/account", async (
            HttpContext ctx,
            DeleteAccountCommandHandler handler,
            IRepositoryDataService repoDataService,
            AnalysisQueue queue,
            CancellationToken cancellationToken) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            // A queued or running job would recreate rows and a clone for an account that no longer exists.
            if ((await repoDataService.GetAllRepositoriesAsync(userId)).Any(r => queue.IsPending(r.Id)))
                return RepositoryEndpoints.AnalysisInProgress();

            var grantRevoked = await handler.Handle(userId, cancellationToken);

            // The cookie names a user who no longer exists; leaving it would sign them straight
            // back into an empty account whose next sign-in recreates the row.
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new AccountDeletedDto { GrantRevoked = grantRevoked });
        })
        .RequireAuthorization()
        .RequireRateLimiting(RateLimitPolicies.Analysis)
        .WithTags("Account")
        .WithName("DeleteAccount");
    }
}
