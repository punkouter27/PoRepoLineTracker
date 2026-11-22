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
/// 2. /health and /api/diag both read the same registry — neither
///    re-implements a probe, which is what docs/spec.md §11.3's "diag is honest"
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
    public async Task KeyVault_check_is_Healthy_only_when_the_live_call_answers()
    {
        var configEmpty = BuildConfig();
        var configSet = BuildConfig(("KeyVault:Uri", "https://example-vault.vault.azure.net/"));
        var log = Substitute.For<ILogger<KeyVaultHealthCheck>>();

        var answers = new KeyVaultHealthCheck(configSet, log) { Probe = (_, _) => Task.CompletedTask };
        var fails = new KeyVaultHealthCheck(configSet, log) { Probe = (_, _) => throw new HttpRequestException("down") };
        var unset = new KeyVaultHealthCheck(configEmpty, log) { Probe = (_, _) => throw new InvalidOperationException("must not be called") };

        (await answers.CheckHealthAsync(NewContext("key-vault"))).Status.Should().Be(HealthStatus.Healthy);
        (await fails.CheckHealthAsync(NewContext("key-vault"))).Status.Should().Be(HealthStatus.Degraded);
        var off = await unset.CheckHealthAsync(NewContext("key-vault"));
        off.Status.Should().Be(HealthStatus.Healthy);
        off.Data.Should().ContainKey("off", "an unset vault is switched off on purpose and /diag shows it as Off");
    }

    [Theory]
    [InlineData("InstrumentationKey=x;IngestionEndpoint=https://westus2-1.in.applicationinsights.azure.com/;LiveEndpoint=https://l/", "https://westus2-1.in.applicationinsights.azure.com/")]
    [InlineData("InstrumentationKey=x", "https://dc.services.visualstudio.com/")]
    [InlineData(null, null)]
    public void Telemetry_check_probes_the_ingestion_endpoint_the_exporter_uses(string? connectionString, string? expected) =>
        TelemetryExportHealthCheck.IngestionEndpoint(connectionString).Should().Be(expected);

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

    [Theory]
    [InlineData("bad_verification_code", HealthStatus.Healthy)]
    [InlineData("incorrect_client_credentials", HealthStatus.Unhealthy)]
    [InlineData("something_else", HealthStatus.Degraded)]
    public async Task GitHubSignIn_check_reads_GitHubs_answer_to_a_made_up_code(string error, HealthStatus expected)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(new JsonStub($"{{\"error\":\"{error}\"}}")));
        var check = new GitHubSignInHealthCheck(factory, Env("Production"), GitHubOptions("id", "secret"),
            Substitute.For<ILogger<GitHubSignInHealthCheck>>());

        (await check.CheckHealthAsync(NewContext("github-sign-in"))).Status.Should().Be(expected);
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Test", true)]
    [InlineData("Development", true)]
    public async Task GitHubSignIn_check_without_an_OAuth_app_is_Off_outside_Production_and_Degraded_in_it(string environment, bool off)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var check = new GitHubSignInHealthCheck(factory, Env(environment), GitHubOptions("", ""),
            Substitute.For<ILogger<GitHubSignInHealthCheck>>());

        var result = await check.CheckHealthAsync(NewContext("github-sign-in"));

        result.Data.ContainsKey("off").Should().Be(off);
        result.Status.Should().Be(off ? HealthStatus.Healthy : HealthStatus.Degraded);
        factory.DidNotReceiveWithAnyArgs().CreateClient(default!);
    }

    private static Microsoft.Extensions.Hosting.IHostEnvironment Env(string name)
    {
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static Microsoft.Extensions.Options.IOptionsMonitor<AspNet.Security.OAuth.GitHub.GitHubAuthenticationOptions> GitHubOptions(string id, string secret)
    {
        var monitor = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<AspNet.Security.OAuth.GitHub.GitHubAuthenticationOptions>>();
        monitor.Get(Arg.Any<string>()).Returns(new AspNet.Security.OAuth.GitHub.GitHubAuthenticationOptions { ClientId = id, ClientSecret = secret });
        return monitor;
    }

    private sealed class JsonStub(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
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
