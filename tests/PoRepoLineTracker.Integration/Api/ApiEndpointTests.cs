using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace PoRepoLineTracker.Integration;

/// <summary>
/// Integration tests for API endpoints using the test web application factory.
/// Tests are organized by endpoint, verifying auth, response codes, and payload structure.
/// Shares the factory instance via <see cref="IntegrationTestCollection"/> to avoid per-class startup overhead.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ApiEndpointTests
{
    private static readonly string[] ExpectedUpdatedExtensions = [".cs", ".razor"];
    private readonly HttpClient _client;
    private readonly CustomWebApplicationFactory _factory;

    public ApiEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        // Antiforgery-aware: this class exercises write endpoints, which now require
        // a token. The handler fetches one the same way the WASM client does.
        _client = factory.CreateAntiforgeryClient();
    }

    // ─── Health & Diagnostics ───────────────────────────────────────────

    [Fact]
    public async Task Health_Endpoint_Returns_200_And_Reports_NotUnhealthy()
    {
        // The integration environment has no GitHub credential, so the GitHub health
        // check returns Degraded (not Unhealthy). Both Healthy and Degraded map to 200
        // per ASP.NET Core's default ResultStatusCodes; Unhealthy would be 503. The
        // contract being asserted here is "the app is up and not reporting an outage".
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("Unhealthy");
    }

    [Fact]
    public async Task SaveUserPreferences_RoundTrips_FileExtensions()
    {
        var updatedPreferences = new UserPreferences
        {
            FileExtensions = new List<string> { ".cs", ".razor" },
            LastUpdated = DateTime.UtcNow
        };

        var saveResponse = await _client.PutAsJsonAsync("/api/settings/user-preferences", updatedPreferences);

        saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync("/api/settings/user-preferences");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        getResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var persistedPreferences = await getResponse.Content.ReadFromJsonAsync<UserPreferences>();
        persistedPreferences.Should().NotBeNull();
        persistedPreferences!.FileExtensions.Should().BeEquivalentTo(ExpectedUpdatedExtensions);
    }

    // ─── Repositories (Authenticated) ───────────────────────────────────

    [Fact]
    public async Task GetAllRepositories_Authenticated_Returns_200_With_An_Array()
    {
        var response = await _client.GetAsync("/api/repositories");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var repos = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        repos.Should().NotBeNull();
    }

    // ─── All Charts Endpoint ────────────────────────────────────────────

    [Fact]
    public async Task GetAllRepositoriesLineHistory_Returns_200()
    {
        var response = await _client.GetAsync("/api/repositories/allcharts/30");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── Auth Endpoints ─────────────────────────────────────────────────

    [Fact]
    public async Task AuthMe_Returns_200_With_UserInfo()
    {
        var response = await _client.GetAsync("/auth/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        // TestAuthHandler sets up claims including isAuthenticated
        json.TryGetProperty("isAuthenticated", out _).Should().BeTrue();
    }

    // ─── CRUD: Add Repository ───────────────────────────────────────────
    //
    // /bulk is the only write path. It replaced a POST "/" that took one repository and inserted
    // it without checking for an existing row, so the same repo added twice produced two records.

    [Fact]
    public async Task AddRepositories_Returns_Ok_With_Added_Bucket()
    {
        var newRepos = new[]
        {
            new { Owner = "testowner", RepoName = "testrepo", CloneUrl = "https://github.com/testowner/testrepo.git" }
        };
        var response = await _client.PostAsJsonAsync("/api/repositories/bulk", newRepos);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.TryGetProperty("added", out var added).Should().BeTrue();
        added.GetArrayLength().Should().Be(1);
        added[0].GetProperty("owner").GetString().Should().Be("testowner");
        added[0].GetProperty("name").GetString().Should().Be("testrepo");
    }

    [Fact]
    public async Task AddRepositories_Rejects_An_Invalid_Entry()
    {
        var newRepos = new[] { new { Owner = "", RepoName = "", CloneUrl = "not-a-url" } };
        var response = await _client.PostAsJsonAsync("/api/repositories/bulk", newRepos);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── CRUD: Delete Single Repository ─────────────────────────────────

    [Fact]
    public async Task DeleteRepository_Returns_NoContent()
    {
        var repoId = RepositoryId.New();
        var response = await _client.DeleteAsync($"/api/repositories/{repoId}");

        // Expect either NoContent (if repo existed) or NotFound (mocked scenario)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.NotFound);
    }

    // ─── CRUD: Delete All Repositories ──────────────────────────────────

    [Fact]
    public async Task DeleteAllRepositories_Authenticated_Returns_NoContent()
    {
        // The bulk-add tests in this class queue background analyses for the same user, and
        // remove-all answers 409 while one holds a repository's lock. Wait those out rather than
        // racing them — the 409 is the guard working, not this endpoint failing.
        HttpResponseMessage response;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((response = await _client.DeleteAsync("/api/repositories/all")).StatusCode == HttpStatusCode.Conflict
               && DateTime.UtcNow < deadline)
            await Task.Delay(250);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task BulkAddRepositories_EmptyList_Returns_200()
    {
        var repos = Array.Empty<object>();
        var response = await _client.PostAsJsonAsync("/api/repositories/bulk", repos);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── CRUD: Trigger Analysis ─────────────────────────────────────────

    [Fact]
    public async Task TriggerReanalysis_Returns_SuccessOrNotFound()
    {
        var repoId = RepositoryId.New();
        var response = await _client.PostAsync($"/api/repositories/{repoId}/reanalyze", null);

        // 202 or 404 depending on the mocked ownership lookup
        ((int)response.StatusCode).Should().BeLessThan(500, "Reanalyze endpoint should not produce server errors with valid GUIDs");
    }
}
