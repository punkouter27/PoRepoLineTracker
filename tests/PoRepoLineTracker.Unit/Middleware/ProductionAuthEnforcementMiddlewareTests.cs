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

    [Fact]
    public async Task OutsideProduction_PassesThrough()
    {
        await InvokeAsync("Development", "/insights");
        _nextCalled.Should().BeTrue();

        _nextCalled = false;
        await InvokeAsync("Test", "/insights");
        _nextCalled.Should().BeTrue();
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
    public async Task Production_LoginPageAndPublicEndpoints_PassThrough()
    {
        await InvokeAsync("Production", "/login");
        _nextCalled.Should().BeTrue();

        _nextCalled = false;
        await InvokeAsync("Production", "/health");
        _nextCalled.Should().BeTrue();

        _nextCalled = false;
        await InvokeAsync("Production", "/auth/login");
        _nextCalled.Should().BeTrue();

        // README badges are fetched by GitHub's image proxy, which cannot follow a sign-in redirect.
        _nextCalled = false;
        await InvokeAsync("Production", "/badge/7f8c1c1e-0000-0000-0000-000000000001/lines.svg");
        _nextCalled.Should().BeTrue();

        _nextCalled = false;
        await InvokeAsync("Production", "/share/7f8c1c1e-0000-0000-0000-000000000001");
        _nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Production_PathsSharingPrefix_AreStillGated()
    {
        var context = await InvokeAsync("Production", "/loginfoo");
        context.Response.StatusCode.Should().Be(StatusCodes.Status302Found);

        var context2 = await InvokeAsync("Production", "/healthcheck-public");
        context2.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
    }

    [Fact]
    public async Task Production_ApiAndAuthenticatedRequests_PassThrough()
    {
        _nextCalled = false;
        var apiContext = await InvokeAsync("Production", "/api/repositories");
        _nextCalled.Should().BeTrue();
        apiContext.Response.StatusCode.Should().NotBe(StatusCodes.Status302Found);

        _nextCalled = false;
        await InvokeAsync("Production", "/hubs/analysis");
        _nextCalled.Should().BeTrue();

        _nextCalled = false;
        await InvokeAsync("Production", "/insights", authenticated: true);
        _nextCalled.Should().BeTrue();
    }
}
