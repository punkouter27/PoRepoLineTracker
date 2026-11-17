using System.Net;
using FluentAssertions;

namespace PoRepoLineTracker.E2EAPI;

/// <summary>
/// Pure API E2E: exercises the running system over HTTP, no UI involved.
/// Covers the two endpoints the deploy smoke test depends on: /health must answer 200
/// without a credential, and /auth/me must answer a well-formed "not signed in" to an anonymous
/// caller rather than a 401.
/// </summary>
public sealed class HealthAndAuthApiTests
{
    [SkippableFact]
    public async Task Health_IsAnonymous_Returns_200_And_ReportsNotUnhealthy()
    {
        // The FallbackPolicy authenticates by default; /health opts out explicitly
        // because Azure's probe and the CI smoke test call it with no credential.
        var response = await E2EApiClient.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "an anonymous probe must get 200, never a 401 or a redirect to sign-in");
        // Healthy or Degraded, both 200 by design: a machine without Key Vault or a GitHub PAT is
        // Degraded (KeyVaultHealthCheck, GitHubApiHealthCheck), and that is not an outage.
        (await response.Content.ReadAsStringAsync()).Should().BeOneOf("Healthy", "Degraded");
    }

    [SkippableFact]
    public async Task AuthMe_Anonymous_Returns_200_ReportingSignedOut()
    {
        var response = await E2EApiClient.GetAsync("/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("isAuthenticated");
        body.Should().Contain("false", "an anonymous caller has no session");
    }

    [SkippableFact]
    public async Task AuthLogin_Anonymous_ChallengesOrReportsNoProvider()
    {
        // With a provider configured this redirects to the provider; with none configured the
        // endpoint answers 503 rather than a generic 500 (see AuthEndpoints).
        var response = await E2EApiClient.GetAsync("/auth/login");

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.Found,
            HttpStatusCode.Redirect,
            HttpStatusCode.ServiceUnavailable);
    }

    [SkippableFact]
    public async Task AuthLogout_Anonymous_RedirectsToLogin()
    {
        var response = await E2EApiClient.GetAsync("/auth/logout");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location?.ToString().Should().Contain("/login");
    }
}
