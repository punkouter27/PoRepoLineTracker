using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace PoRepoLineTracker.API.Features.Repositories;

/// <summary>
/// Push webhooks: GitHub tells the app about a push, and the app queues an incremental analysis.
///
/// <para><b>Why it works with no setup.</b> The 2026-09 receiver was removed because nothing ever
/// registered a hook or configured its secret. Here the app does both. Each repository gets its own
/// random secret, stored on its row, and a hook registered through the GitHub API with the owner's
/// token (the <c>repo</c> scope covers hooks). The callback host comes from <c>WEBSITE_HOSTNAME</c>,
/// which App Service sets on its own. Without it (a local run, where GitHub could not reach the
/// app anyway) webhooks are simply off.</para>
///
/// <para>Registration runs after each analysis (<see cref="AnalysisQueue"/>), so new, re-analysed
/// and resumed repositories all pick up a hook. Repositories the user cannot administer refuse it,
/// and that is fine: they still update on a manual re-analyse. Everything here is best-effort,
/// and a failure is never an analysis failure.</para>
/// </summary>
public sealed class RepositoryWebhooks(
    IHttpClientFactory httpClientFactory,
    IUserService userService,
    IConfiguration configuration,
    ILogger<RepositoryWebhooks> logger)
{
    /// <summary>Whether this deployment has a public host GitHub can deliver to.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(configuration[ConfigKeys.Hosting.PublicHostName]);

    private string CallbackUrl(RepositoryId id) =>
        $"https://{configuration[ConfigKeys.Hosting.PublicHostName]}/api/webhooks/github/{id}";

    /// <summary>Registers a push hook if the repository has none yet. Never throws.</summary>
    public async Task EnsureAsync(GitHubRepository repository, IRepositoryDataService dataService, CancellationToken cancellationToken)
    {
        if (!Enabled || repository.WebhookId is not null) return;

        try
        {
            var token = await userService.ResolveGitHubTokenAsync(repository.UserId);
            if (string.IsNullOrEmpty(token)) return;

            var secret = RandomNumberGenerator.GetHexString(40, lowercase: true);
            var hookId = await CreateAsync(repository, token, secret, cancellationToken);

            // 422 means a hook with this URL already exists: one this app created but whose row
            // write was lost, so its secret is unknown. Replace it rather than leave a hook that
            // fails every delivery.
            if (hookId is null && await DeleteByUrlAsync(repository, token, cancellationToken))
                hookId = await CreateAsync(repository, token, secret, cancellationToken);

            if (hookId is null) return;

            repository.WebhookId = hookId;
            repository.WebhookSecret = secret;
            await dataService.UpdateRepositoryAsync(repository);
            logger.LogInformation("Registered push webhook {HookId} on {Owner}/{Name}", hookId, repository.Owner, repository.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not register a webhook on {Owner}/{Name}", repository.Owner, repository.Name);
        }
    }

    /// <summary>Removes the repository's hook from GitHub. Never throws; call before the user's token is deleted.</summary>
    public async Task RemoveAsync(GitHubRepository repository)
    {
        if (repository.WebhookId is not { } hookId) return;

        try
        {
            var token = await userService.ResolveGitHubTokenAsync(repository.UserId);
            if (string.IsNullOrEmpty(token)) return;

            using var response = await SendAsync(HttpMethod.Delete, $"{HooksPath(repository)}/{hookId}", token, null, CancellationToken.None);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                logger.LogWarning("GitHub refused to delete webhook {HookId} on {Owner}/{Name}: {Status}", hookId, repository.Owner, repository.Name, response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete webhook {HookId} on {Owner}/{Name}", hookId, repository.Owner, repository.Name);
        }
    }

    /// <returns>The new hook's id; null on 422 (already exists). Other refusals are logged and also null.</returns>
    private async Task<long?> CreateAsync(GitHubRepository repository, string token, string secret, CancellationToken cancellationToken)
    {
        var body = new GitHubHookRequest("web", true, ["push"], new GitHubHookConfig(CallbackUrl(repository.Id), "json", secret, "0"));

        using var response = await SendAsync(HttpMethod.Post, HooksPath(repository), token,
            JsonContent.Create(body, ApiJsonContext.Default.GitHubHookRequest), cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return doc.RootElement.GetProperty("id").GetInt64();
        }

        if (response.StatusCode != HttpStatusCode.UnprocessableEntity)
            logger.LogInformation("GitHub refused a webhook on {Owner}/{Name}: {Status} (needs admin rights on the repository)", repository.Owner, repository.Name, response.StatusCode);
        return null;
    }

    private async Task<bool> DeleteByUrlAsync(GitHubRepository repository, string token, CancellationToken cancellationToken)
    {
        using var list = await SendAsync(HttpMethod.Get, $"{HooksPath(repository)}?per_page=100", token, null, cancellationToken);
        if (!list.IsSuccessStatusCode) return false;

        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync(cancellationToken));
        var url = CallbackUrl(repository.Id);
        var deleted = false;
        foreach (var hook in doc.RootElement.EnumerateArray())
        {
            if (hook.TryGetProperty("config", out var config) && config.TryGetProperty("url", out var u) && u.GetString() == url)
            {
                using var _ = await SendAsync(HttpMethod.Delete, $"{HooksPath(repository)}/{hook.GetProperty("id").GetInt64()}", token, null, cancellationToken);
                deleted = true;
            }
        }
        return deleted;
    }

    private static string HooksPath(GitHubRepository r) => $"repos/{Uri.EscapeDataString(r.Owner)}/{Uri.EscapeDataString(r.Name)}/hooks";

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, HttpContent? content, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        // Set on the request, so it replaces the client's default PAT header rather than joining it.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return httpClientFactory.CreateClient("GitHubClient").SendAsync(request, cancellationToken);
    }

    /// <summary>GitHub's <c>X-Hub-Signature-256</c> check: HMAC-SHA256 of the raw body, compared in constant time.</summary>
    public static bool VerifySignature(ReadOnlySpan<byte> body, string? signatureHeader, string secret)
    {
        const string prefix = "sha256=";
        if (string.IsNullOrEmpty(secret) || signatureHeader is null || !signatureHeader.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        Span<byte> expected = stackalloc byte[32];
        try
        {
            if (Convert.FromHexString(signatureHeader.AsSpan(prefix.Length), expected, out _, out var written) != System.Buffers.OperationStatus.Done || written != 32)
                return false;
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body), expected);
    }
}

/// <summary>The receiver GitHub delivers pushes to; see <see cref="RepositoryWebhooks"/>.</summary>
internal static class WebhookEndpoints
{
    /// <summary>Largest push payload read. GitHub caps deliveries at 25 MB; a push big enough to pass this is not worth a live update.</summary>
    private const long MaxPayloadBytes = 5 * 1024 * 1024;

    internal static void MapWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Under /api so the production page gate and the service worker already leave it alone.
        endpoints.MapPost("/api/webhooks/github/{repositoryId}", async (
            RepositoryId repositoryId,
            HttpContext ctx,
            IRepositoryDataService dataService,
            AnalysisQueue queue,
            CancellationToken cancellationToken) =>
        {
            var repository = await dataService.GetRepositoryByIdAsync(repositoryId);
            if (repository?.WebhookSecret is not { Length: > 0 } secret)
                return Results.NotFound();

            using var buffer = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(buffer, cancellationToken);
            if (!RepositoryWebhooks.VerifySignature(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), ctx.Request.Headers["X-Hub-Signature-256"], secret))
                return Results.Unauthorized();

            // "ping" arrives once when the hook is created; anything but a push is not ours to act on.
            if (ctx.Request.Headers["X-GitHub-Event"] != "push")
                return Results.Ok();

            buffer.Position = 0;
            using (var doc = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken))
            {
                var root = doc.RootElement;
                var defaultBranch = root.TryGetProperty("repository", out var repo) && repo.TryGetProperty("default_branch", out var b) ? b.GetString() : null;
                if (!root.TryGetProperty("ref", out var pushedRef) || pushedRef.GetString() != $"refs/heads/{defaultBranch}")
                    return Results.Ok(); // a feature branch; the charts follow the default branch only
            }

            // Pending means a run is queued or underway, and it may already have pulled. Ask for
            // one more run after it so this push is not missed.
            if (!await queue.EnqueueReanalysisAsync(repository, dataService, full: false))
                queue.RunAgainAfterCurrent(repositoryId);

            return Results.Accepted();
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Public)
        .WithMetadata(new SkipAntiforgeryAttribute("GitHub authenticates with the X-Hub-Signature-256 HMAC, not a browser antiforgery token"))
        .WithMetadata(new RequestSizeLimitAttribute(MaxPayloadBytes))
        .ExcludeFromDescription()
        .WithName("GitHubPushWebhook");
    }
}

/// <summary>Body of <c>POST /repos/{owner}/{repo}/hooks</c>, named as GitHub spells it.</summary>
internal sealed record GitHubHookRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("events")] string[] Events,
    [property: JsonPropertyName("config")] GitHubHookConfig Config);

internal sealed record GitHubHookConfig(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("secret")] string Secret,
    [property: JsonPropertyName("insecure_ssl")] string InsecureSsl);
