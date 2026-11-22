using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace PoRepoLineTracker.Integration;

/// <summary>
/// Unlike <see cref="CustomWebApplicationFactory"/>, this factory does NOT replace the
/// authentication stack — the point of these tests is to exercise the real
/// <see cref="FakeAuthHandler"/> the app ships. Storage is still mocked so the host
/// needs no Azurite.
/// </summary>
public class RealAuthFactory : WebApplicationFactory<Program>
{
    public const string KnownSecret = "test-pat-value-must-never-be-echoed";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "GitHub:ClientId", "test-client-id" },
                { "GitHub:ClientSecret", "test-client-secret" },
                { "GitHub:PAT", KnownSecret },
                { "KeyVault:Uri", "" },
                { "OpenTelemetry:OtlpEndpoint", "" },
                { "EnableConsoleExporters", "false" },
                { "APPLICATIONINSIGHTS_CONNECTION_STRING", "" },
                { "ApplicationInsights:ConnectionString", "" },
                { "AzureTableStorage:ConnectionString", "UseDevelopmentStorage=true" },
                { "GitHub:LocalReposPath", CustomWebApplicationFactory.TestRepoRoot }
            });
        });

        builder.ConfigureServices(services =>
        {
            // Storage is irrelevant to auth/diag behaviour — mock it so no container is needed.
            services.RemoveAll<IRepositoryDataService>();
            services.RemoveAll<IUserService>();
            services.RemoveAll<IUserPreferencesService>();
            services.RemoveAll<IGitHubService>();

            var repoData = Substitute.For<IRepositoryDataService>();
            repoData.GetAllRepositoriesAsync(Arg.Any<UserId>())
                .Returns(Task.FromResult(Enumerable.Empty<GitHubRepository>()));

            services.AddScoped(_ => repoData);
            services.AddScoped(_ => Substitute.For<IUserService>());
            services.AddScoped(_ => Substitute.For<IUserPreferencesService>());
            services.AddScoped(_ => Substitute.For<IGitHubService>());

            // The GitHub health check must not reach api.github.com from a test.
            services.AddHttpClient("GitHubClient").ConfigurePrimaryHttpMessageHandler(() => new StubGitHubHandler());
            // Nor may the live service probes (GitHub sign-in token endpoint): GitHub's answer to a made-up code.
            services.AddHttpClient("DiagProbe").ConfigurePrimaryHttpMessageHandler(() => new StubJsonHandler("{\"error\":\"bad_verification_code\"}"));
        });

        builder.UseEnvironment("Test");
    }
}

/// <summary>Answers any request with 200 and a fixed JSON body.</summary>
internal sealed class StubJsonHandler(string json) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
}

/// <summary>Answers the GitHub health probe locally: 200 with a healthy rate-limit budget.</summary>
internal sealed class StubGitHubHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("X-RateLimit-Remaining", "5000");
        return Task.FromResult(response);
    }
}

/// <summary>
/// Header-driven dev/test auth, and the anonymous /api/diag behind the /diag page.
/// </summary>
public class FakeAuthAndDiagTests : IClassFixture<RealAuthFactory>
{
    private readonly RealAuthFactory _factory;

    public FakeAuthAndDiagTests(RealAuthFactory factory) => _factory = factory;

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static HttpRequestMessage Get(string url, string? user = null, string? roles = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", "application/json");
        if (user is not null) request.Headers.Add(FakeAuthHandler.UserHeader, user);
        if (roles is not null) request.Headers.Add(FakeAuthHandler.RolesHeader, roles);
        return request;
    }

    // ── Validators ───────────────────────────────────────────────────────────────────

    /// <summary>The endpoints' validators are registered by type (no assembly scanning), so the host resolves them.</summary>
    [Fact]
    public void Validators_Are_Registered_By_Type()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<FluentValidation.IValidator<PoRepoLineTracker.Shared.Models.Dtos.BulkRepositoryDto>>()
            .Should().BeOfType<PoRepoLineTracker.Shared.Models.BulkRepositoryDtoValidator>();
        scope.ServiceProvider.GetRequiredService<FluentValidation.IValidator<PoRepoLineTracker.Shared.Domain.UserPreferences>>()
            .Should().BeOfType<PoRepoLineTracker.Shared.Models.UserPreferencesValidator>();
    }

    // ── FakeAuthHandler ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Protected_Endpoint_Without_FakeUser_Header_Is_Rejected()
    {
        var response = await CreateClient().SendAsync(Get("/api/repositories"));
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Found);
    }

    [Fact]
    public async Task Protected_Endpoint_With_FakeUser_Header_Is_Authorized()
    {
        var response = await CreateClient().SendAsync(Get("/api/repositories", user: "alice"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task FakeUser_Accepts_A_Guid_Verbatim()
    {
        var id = "11111111-2222-3333-4444-555555555555";
        var response = await CreateClient().SendAsync(Get("/auth/me", user: id));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(id, "a GUID X-Fake-User must be used as the user id verbatim");
    }

    [Fact]
    public async Task FakeUser_Maps_A_Name_To_A_Stable_Id_Across_Requests()
    {
        var client = CreateClient();
        var first = await (await client.SendAsync(Get("/auth/me", user: "bob"))).Content.ReadAsStringAsync();
        var second = await (await client.SendAsync(Get("/auth/me", user: "bob"))).Content.ReadAsStringAsync();

        first.Should().Be(second, "the same X-Fake-User name must resolve to the same user id");
    }

    [Fact]
    public async Task FakeUser_Maps_Different_Names_To_Different_Ids()
    {
        var client = CreateClient();
        var alice = await (await client.SendAsync(Get("/auth/me", user: "alice"))).Content.ReadAsStringAsync();
        var bob = await (await client.SendAsync(Get("/auth/me", user: "bob"))).Content.ReadAsStringAsync();

        alice.Should().NotBe(bob);
    }

    [Fact]
    public async Task FakeRoles_Header_Is_Accepted_Alongside_FakeUser()
    {
        var response = await CreateClient().SendAsync(Get("/api/repositories", user: "alice", roles: "admin,auditor"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Blank_FakeUser_Header_Does_Not_Authenticate()
    {
        var response = await CreateClient().SendAsync(Get("/api/repositories", user: "   "));
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Found);
    }

    [Fact]
    public void FakeAuthHandler_Refuses_To_Initialise_In_Production_ButNotElsewhere()
    {
        var production = Substitute.For<IWebHostEnvironment>();
        production.EnvironmentName.Returns(Environments.Production);

        var act = () => FakeAuthHandler.ThrowIfProduction(production);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must never be registered in Production*");

        var test = Substitute.For<IWebHostEnvironment>();
        test.EnvironmentName.Returns("Test");

        var actOutside = () => FakeAuthHandler.ThrowIfProduction(test);

        actOutside.Should().NotThrow();
    }

    // ── /api/diag ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiDiag_Is_Anonymous_And_Reports_The_Running_Environment_As_Json()
    {
        var response = await CreateClient().SendAsync(Get("/api/diag"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the /diag page must work signed-out");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("environment").GetString().Should().Be("Test");
        json.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("checkedMs").GetDouble().Should().BeGreaterThanOrEqualTo(0);
        var groups = json.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("group").GetString()).ToHashSet();
        groups.Should().Contain("api").And.Contain("service", "the /diag page lists API, Data and Services");
        groups.Should().BeSubsetOf(["api", "data", "service"]);
    }

    /// <summary>
    /// Services switched off on purpose (no Key Vault URI, no telemetry exporter here) show the neutral "Off", made no
    /// call, and do not pull the overall status down; GitHub sign-in is a live probe (stubbed here).
    /// </summary>
    [Fact]
    public async Task ApiDiag_Shows_Unconfigured_Services_As_Off_And_Leaves_Them_Out_Of_The_Overall_Status()
    {
        var json = await (await CreateClient().SendAsync(Get("/api/diag"))).Content.ReadFromJsonAsync<JsonElement>();
        var checks = json.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c);

        foreach (var off in new[] { "key-vault", "telemetry" })
        {
            checks[off].GetProperty("status").GetString().Should().Be("Off", $"{off} is not configured in this test host");
            checks[off].GetProperty("latencyMs").GetDouble().Should().Be(0);
            checks[off].GetProperty("problem").ValueKind.Should().Be(JsonValueKind.Null);
        }

        // No GitHub OAuth scheme ends up registered in this host (AddAuth reads its settings early), and Test is
        // not Production, so sign-in is Off here too; GitHubSignIn_check_* (Unit) covers the live probe's answers.
        checks["github-sign-in"].GetProperty("status").GetString().Should().Be("Off");
        checks["github-api"].GetProperty("detail").GetString().Should().Be("Checked with a live call.");

        var counted = checks.Values.Select(c => c.GetProperty("status").GetString()).Where(s => s != "Off").ToList();
        var expected = counted.Contains("Unhealthy") ? "Unhealthy" : counted.Contains("Degraded") ? "Degraded" : "Healthy";
        json.GetProperty("status").GetString().Should().Be(expected, "Off rows never count toward the overall status");
    }

    /// <summary>
    /// Anonymous, so the payload must carry statuses and fixed text only. Storage here has no
    /// Azurite behind it, so the table check throws and the test also proves that an exception's
    /// MESSAGE (which names the endpoint) never reaches the wire, only its type.
    /// </summary>
    [Fact]
    public async Task ApiDiag_Never_Exposes_Secrets_Or_Internals()
    {
        var body = await (await CreateClient().SendAsync(Get("/api/diag"))).Content.ReadAsStringAsync();

        string[] forbidden =
        [
            RealAuthFactory.KnownSecret, "test-client-secret", "test-client-id",
            "UseDevelopmentStorage", "AccountKey", "devstoreaccount1", "127.0.0.1", "localhost",
            "vault.azure.net", "://", "PoRepoLineTrackerRepositories", CustomWebApplicationFactory.TestRepoRoot,
            ":\\", "   at ", "StackTrace", "@", "tenant", "subscription"
        ];
        foreach (var value in forbidden)
            body.Should().NotContainEquivalentOf(value, $"/api/diag is anonymous and must not leak '{value}'");
    }
}
