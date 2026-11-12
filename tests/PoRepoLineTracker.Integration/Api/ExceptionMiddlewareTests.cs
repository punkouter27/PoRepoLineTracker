using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace PoRepoLineTracker.Integration;

/// <summary>
/// Verifies that unhandled exceptions in the repository line-history pipeline
/// return ProblemDetails JSON (via the endpoint's own try/catch which calls Results.Problem).
/// Uses a custom factory that:
///   1. Mocks IRepositoryDataService so ThrowingRepoId is owned by the test user (passes IDOR check).
///   2. Registers a line-history handler backed by a throwing data service, triggering
///      Results.Problem(500).
/// </summary>
public class ExceptionMiddlewareTests : IClassFixture<ExceptionMiddlewareFactory>
{
    private readonly HttpClient _client;

    public ExceptionMiddlewareTests(ExceptionMiddlewareFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task UnhandledException_Returns_500_ProblemDetails_With_TitleAndDetail()
    {
        // The factory configures GetLineCountHistoryQuery to throw
        var repoId = ExceptionMiddlewareFactory.ThrowingRepoId;
        var response = await _client.GetAsync($"/api/repositories/{repoId}/linehistory/365");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.TryGetProperty("status", out var status).Should().BeTrue();
        status.GetInt32().Should().Be(500);
        json.TryGetProperty("title", out var title).Should().BeTrue();
        title.GetString().Should().NotBeNullOrEmpty();
        json.TryGetProperty("detail", out var detail).Should().BeTrue();
        detail.GetString().Should().NotBeNullOrEmpty();
    }
}

/// <summary>
/// Custom factory that:
///   1. Overrides IRepositoryDataService so ThrowingRepoId is "found" and owned by the test user,
///      allowing the endpoint to proceed past the repo-existence / IDOR checks.
///   2. Registers a GetLineCountHistoryQueryHandler whose data service throws for any query,
///      exercising the endpoint's try/catch which returns Results.Problem (500).
/// Inherits from <see cref="CustomWebApplicationFactory"/> to keep all other service mocks
/// and test authentication active.
/// </summary>
public class ExceptionMiddlewareFactory : CustomWebApplicationFactory
{
    /// <summary>The repository ID that causes the mediator to throw.</summary>
    public static readonly RepositoryId ThrowingRepoId = new(new Guid("DEADBEEF-DEAD-BEEF-DEAD-BEEFDEADBEEF"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            // Mock IRepositoryDataService: return a fake repo owned by the test user for ThrowingRepoId
            // so the endpoint passes its repo-existence and IDOR ownership checks.
            var mockRepoDataService = Substitute.For<IRepositoryDataService>();
            var fakeRepo = new GitHubRepository
            {
                Id = ThrowingRepoId,
                UserId = UserId.Parse(TestAuthHandler.TestUserId, System.Globalization.CultureInfo.InvariantCulture),
                Owner = "testowner",
                Name = "testrepo",
                CloneUrl = "https://github.com/testowner/testrepo.git"
            };
            mockRepoDataService
                .GetRepositoryByIdAsync(ThrowingRepoId)
                .Returns(Task.FromResult<GitHubRepository?>(fakeRepo));

            services.AddScoped<IRepositoryDataService>(_ => mockRepoDataService);

            // Throwing handler: the data service it wraps throws for any line-history query, so
            // the endpoint's try/catch returns Results.Problem(500).
            var throwingDataService = Substitute.For<IRepositoryDataService>();
            throwingDataService
                .GetLineCountHistoryAsync(Arg.Any<RepositoryId>(), Arg.Any<int>())
                .ThrowsAsync(new InvalidOperationException("Deliberate test exception for middleware verification"));

            services.AddScoped(_ => new GetLineCountHistoryQueryHandler(throwingDataService));
        });
    }
}

