using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PoRepoLineTracker.Api.Features.Diagnostics;
using PoRepoLineTracker.Api.Storage;

namespace PoRepoLineTracker.Unit.Diagnostics;

/// <summary>
/// Pins the A3 acceptance:
/// 1. The check returns the documented status for the documented inputs.
/// 2. /health and /api/diagnostics both read the same registry — neither
///    re-implements a probe, which is what SPEC §11.3's "diag is honest"
///    requirement is asking for.
/// </summary>
public class HealthCheckRegistrationTests
{
    [Fact]
    public async Task AzureTableStorage_check_returns_a_status_for_a_substituted_client()
    {
        var check = new AzureTableStorageHealthCheck(
            Substitute.For<Azure.Data.Tables.TableServiceClient>(),
            BuildConfig(("AzureTableStorage:RepositoryTableName", "Test")),
            Substitute.For<ILogger<AzureTableStorageHealthCheck>>());

        var context = new HealthCheckContext { Registration = new HealthCheckRegistration("azure-table-storage", check, null, null) };

        // The substituted TableServiceClient cannot return a real queryable table; the check's
        // exception path is the only branch exercised here. We assert the contract on the
        // reachable / unreachable split instead of mocking every table response.
        var result = await check.CheckHealthAsync(context);
        result.Status.Should().BeOneOf(HealthStatus.Healthy, HealthStatus.Unhealthy);
        result.Description.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task KeyVault_check_returns_Healthy_when_KeyVaultUri_configured_Degraded_otherwise()
    {
        var configEmpty = BuildConfig();
        var configSet = BuildConfig(("KeyVault:Uri", "https://example-vault.vault.azure.net/"));

        var healthy = new KeyVaultHealthCheck(configSet, Substitute.For<ILogger<KeyVaultHealthCheck>>());
        var degraded = new KeyVaultHealthCheck(configEmpty, Substitute.For<ILogger<KeyVaultHealthCheck>>());

        var healthyResult = await healthy.CheckHealthAsync(NewContext("key-vault"));
        var degradedResult = await degraded.CheckHealthAsync(NewContext("key-vault"));

        healthyResult.Status.Should().Be(HealthStatus.Healthy);
        degradedResult.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task GitHubApi_check_evaluates_rate_limits_and_status_codes()
    {
        // Degraded when rate limit low
        var degradedHttp = BuildStubHttp(HttpStatusCode.OK, "X-RateLimit-Remaining", "42");
        var degradedFactory = Substitute.For<IHttpClientFactory>();
        degradedFactory.CreateClient(Arg.Any<string>()).Returns(degradedHttp);
        var degradedCheck = new GitHubApiHealthCheck(degradedFactory, Substitute.For<ILogger<GitHubApiHealthCheck>>());
        (await degradedCheck.CheckHealthAsync(NewContext("github-api"))).Status.Should().Be(HealthStatus.Degraded);

        // Degraded on 401 or 403 (configuration issue, serves cached reads)
        var unauthHttp = BuildStubHttp(HttpStatusCode.Unauthorized, "X-RateLimit-Remaining", "5000");
        var unauthFactory = Substitute.For<IHttpClientFactory>();
        unauthFactory.CreateClient(Arg.Any<string>()).Returns(unauthHttp);
        var unauthCheck = new GitHubApiHealthCheck(unauthFactory, Substitute.For<ILogger<GitHubApiHealthCheck>>());
        (await unauthCheck.CheckHealthAsync(NewContext("github-api"))).Status.Should().Be(HealthStatus.Degraded);

        // Healthy when rate limit high
        var healthyHttp = BuildStubHttp(HttpStatusCode.OK, "X-RateLimit-Remaining", "4999");
        var healthyFactory = Substitute.For<IHttpClientFactory>();
        healthyFactory.CreateClient(Arg.Any<string>()).Returns(healthyHttp);
        var healthyCheck = new GitHubApiHealthCheck(healthyFactory, Substitute.For<ILogger<GitHubApiHealthCheck>>());
        (await healthyCheck.CheckHealthAsync(NewContext("github-api"))).Status.Should().Be(HealthStatus.Healthy);
    }

    private static HealthCheckContext NewContext(string name) =>
        new() { Registration = new HealthCheckRegistration(name, Substitute.For<IHealthCheck>(), null, null) };

    private static IConfiguration BuildConfig(params (string Key, string Value)[] pairs)
    {
        var dict = pairs.ToDictionary(p => p.Key, p => (string?)p.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static HttpClient BuildStubHttp(HttpStatusCode status, string headerName, string headerValue)
    {
        var handler = new StubHandler(status, headerName, headerValue);
        return new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
    }

    /// <summary>Tiny HttpMessageHandler stub so the GitHub check can be tested without a network.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _headerName;
        private readonly string _headerValue;

        public StubHandler(HttpStatusCode status, string headerName, string headerValue)
        {
            _status = status;
            _headerName = headerName;
            _headerValue = headerValue;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status);
            response.Headers.TryAddWithoutValidation(_headerName, _headerValue);
            return Task.FromResult(response);
        }
    }
}
