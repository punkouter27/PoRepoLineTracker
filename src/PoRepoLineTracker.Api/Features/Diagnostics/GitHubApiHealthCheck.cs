using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PoRepoLineTracker.Api.Features.Diagnostics;

/// <summary>
/// Health check for the GitHub REST API. Rate-limit aware: returns
/// <see cref="HealthStatus.Degraded"/> when the <c>X-RateLimit-Remaining</c> header reports a
/// budget below the safety threshold (<c>100</c>) — the app should keep serving reads but the
/// operator should know bulk operations will start failing.
///
/// <para>Uses <see cref="IHttpClientFactory"/> rather than a captured <see cref="HttpClient"/>:
/// the named client the factory hands back carries the standard resilience pipeline (rate
/// limiter, retry, circuit breaker) registered in <c>InfrastructureServiceExtensions</c>, so
/// a wedged GitHub does not wedge the health endpoint.</para>
/// </summary>
public sealed class GitHubApiHealthCheck(
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubApiHealthCheck> logger) : IHealthCheck
{
    /// <summary>
    /// Below this many remaining requests the app is one burst away from being rate-limited
    /// outright. Degraded (not Unhealthy) so /health still returns 200 — the API is reachable,
    /// it is just running on fumes.
    /// </summary>
    private const int RateLimitRemainingDegradedThreshold = 100;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // A HEAD against /rate_limit is the canonical "is GitHub reachable, what is our budget"
        // probe — no scope, no body, no quota cost. The GitHubClient named client registers a
        // BaseAddress of https://api.github.com/, so the relative path resolves correctly.
        var http = httpClientFactory.CreateClient("GitHubClient");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/rate_limit");
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            // Degraded, not Unhealthy: the app can still serve cached reads; the operator
            // just shouldn't queue new bulk work. Unhealthy here would also flip /health to
            // 503, which is the wrong answer for "the third-party API is briefly unreachable".
            // An Azure load balancer or k8s probe would take the pod out of rotation for an
            // outage that isn't this app's fault.
            logger.LogWarning(ex, "GitHub API health check failed to reach the endpoint");
            return HealthCheckResult.Degraded("GitHub API is unreachable", ex);
        }

        using (response)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                // Degraded, not Unhealthy: this is a configuration issue (no credential, or
                // a revoked one), not an outage. The app can still serve cached reads;
                // operator needs to fix the credential. Unhealthy here would flip /health
                // to 503 and tell a load balancer to take this pod out of rotation, which
                // would mask an unrelated bug with a configuration complaint.
                return HealthCheckResult.Degraded(
                    $"GitHub API returned {response.StatusCode} — credential missing or revoked");
            }

            if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var values))
            {
                // GitHub's headers are not visible behind some proxies / response shapes. Treat
                // the probe as Healthy when the request succeeded — the rate-limit budget is
                // useful but not the only signal of API health.
                return HealthCheckResult.Healthy("GitHub API reachable (rate-limit header not present)");
            }

            if (!int.TryParse(values.First(), out var remaining))
            {
                return HealthCheckResult.Healthy($"GitHub API reachable (rate-limit header unparseable: '{values.First()}')");
            }

            return remaining < RateLimitRemainingDegradedThreshold
                ? HealthCheckResult.Degraded($"GitHub API rate limit is low: {remaining} remaining")
                : HealthCheckResult.Healthy($"GitHub API healthy: {remaining} requests remaining");
        }
    }
}
