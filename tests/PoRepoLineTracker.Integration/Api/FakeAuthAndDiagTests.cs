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
        });

        builder.UseEnvironment("Test");
    }
}

/// <summary>
/// Header-driven dev/test auth, and /api/diagnostics as the single diagnostics surface.
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

    // ── /api/diagnostics ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiDiagnostics_Requires_Authentication()
    {
        var response = await CreateClient().SendAsync(Get("/api/diagnostics"));
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Found);
    }

    [Fact]
    public async Task ApiDiagnostics_Never_Echoes_A_Configured_Secret_Value()
    {
        var response = await CreateClient().SendAsync(Get("/api/diagnostics", user: "alice"));
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain(RealAuthFactory.KnownSecret,
            "the diagnostics payload reports configured/not-configured, never a secret value");
    }

    [Fact]
    public async Task ApiDiagnostics_Reports_The_Running_Environment_As_Json()
    {
        var response = await CreateClient().SendAsync(Get("/api/diagnostics", user: "alice"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("environment").GetString().Should().Be("Test");
    }
}
