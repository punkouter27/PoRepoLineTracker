using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using NSubstitute;
using PoRepoLineTracker.API.Middleware;

namespace PoRepoLineTracker.Unit;

/// <summary>
/// The production sign-in gate, tested directly because nothing else can reach it.
///
/// <para>This middleware is a no-op outside Production, and every tier that drives a real host —
/// Integration ("Test"), E2EAPI and E2EUI (Development) — therefore runs straight past it. That is
/// exactly how it came to send every unauthenticated page request to github.com: local said 200,
/// the deployed site said 302 to another origin, and no test could see the difference. Unit tests
/// against a substituted environment are the only place the Production branch executes.</para>
/// </summary>
public class ProductionAuthEnforcementMiddlewareTests
{
    private bool _nextCalled;

    private ProductionAuthEnforcementMiddleware Build(string environmentName)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns(environmentName);

        return new ProductionAuthEnforcementMiddleware(
            _ => { _nextCalled = true; return Task.CompletedTask; },
            env,
            Substitute.For<ILogger<ProductionAuthEnforcementMiddleware>>());
    }

    private static DefaultHttpContext Request(string path, string query = "", bool authenticated = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);

        if (authenticated)
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "someone")], "test"));

        return context;
    }

    private async Task<DefaultHttpContext> InvokeAsync(string environment, string path, string query = "", bool authenticated = false)
    {
        var context = Request(path, query, authenticated);
        await Build(environment).InvokeAsync(context);
        return context;
    }

    /// <summary>
    /// Everything the gate must let through: any request outside Production; in Production the
    /// login page and the anonymous endpoints (README badges are fetched by GitHub's image proxy,
    /// which cannot follow a sign-in redirect), /api and /hubs (they answer 401 themselves), and
    /// any signed-in request.
    /// </summary>
    [Theory]
    [InlineData("Development", "/insights", false)]
    [InlineData("Test", "/insights", false)]
    [InlineData("Production", "/login", false)]
    [InlineData("Production", "/health", false)]
    [InlineData("Production", "/auth/login", false)]
    [InlineData("Production", "/badge/7f8c1c1e-0000-0000-0000-000000000001/lines.svg", false)]
    [InlineData("Production", "/share/7f8c1c1e-0000-0000-0000-000000000001", false)]
    [InlineData("Production", "/api/repositories", false)]
    [InlineData("Production", "/hubs/analysis", false)]
    [InlineData("Production", "/insights", true)]
    public async Task PassesThrough(string environment, string path, bool authenticated)
    {
        var context = await InvokeAsync(environment, path, authenticated: authenticated);

        _nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().NotBe(StatusCodes.Status302Found);
    }

    [Fact]
    public async Task Production_UnauthenticatedPage_RedirectsToLoginPageWithRelativeReturnUrl()
    {
        var context = await InvokeAsync("Production", "/repositories/2025", "?tab=languages");

        context.Response.StatusCode.Should().Be(StatusCodes.Status302Found);

        var location = context.Response.Headers.Location.ToString();
        location.Should().StartWith("/login?returnUrl=");
        location.Should().NotContain("github.com");

        var returnUrl = Uri.UnescapeDataString(location.Split("returnUrl=")[1]);
        Uri.IsWellFormedUriString(returnUrl, UriKind.Absolute).Should().BeFalse();
        returnUrl.Should().StartWith("/repositories/2025?tab=languages");
        _nextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Production_PathsSharingPrefix_AreStillGated()
    {
        var context = await InvokeAsync("Production", "/loginfoo");
        context.Response.StatusCode.Should().Be(StatusCodes.Status302Found);

        var context2 = await InvokeAsync("Production", "/healthcheck-public");
        context2.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
    }
}
