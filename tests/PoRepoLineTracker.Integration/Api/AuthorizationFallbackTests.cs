using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PoRepoLineTracker.Integration;

/// <summary>
/// Proves the server-side FallbackPolicy denies by default: an unauthenticated caller
/// is refused everywhere except the routes that explicitly opt out with AllowAnonymous.
///
/// Shares the collection's single host and signs out per request via
/// <see cref="TestAuthHandler.AnonymousHeader"/>. A second WebApplicationFactory would race the
/// entry-point resolver ("Server hasn't been initialized yet") — the same class of failure as
/// regression R2.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AuthorizationFallbackTests
{
    // Every route that opts out of the FallbackPolicy. A new public route fails
    // Anonymous_surface_is_exactly_the_allow_list until it is added here on purpose. Static files
    // and the Blazor framework files are public by design and are identified by their metadata.
    private static readonly string[] AnonymousAllowList =
    [
        "/api/antiforgery/token",
        "/auth/login",
        "/auth/logout",
        "/auth/me",
        "/badge/{repositoryId:guid}/{kind}.svg",
        "/health",
        "/health/live",
        "/share/{repositoryId:guid}",
        "/{*path:nonfile}",
    ];

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _anonymous;

    public AuthorizationFallbackTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _anonymous = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        _anonymous.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "true");
    }

    // ─── Denied without a session ───────────────────────────────────────

    [Theory]
    [InlineData("/api/repositories")]
    [InlineData("/api/repositories/allcharts/30")]
    public async Task ProtectedEndpoint_Anonymous_IsDenied(string route)
    {
        var response = await _anonymous.GetAsync(route);

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden,
            HttpStatusCode.Found);
    }

    [Fact]
    public async Task UnknownApiRoute_Anonymous_Returns_404()
    {
        // Exactly 404, which is what the deploy smoke test asserts. "< 500" passed locally on
        // the day production answered 500 here.
        var response = await _anonymous.GetAsync("/api/definitely-not-a-route");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Explicitly public ──────────────────────────────────────────────

    [Fact]
    public void Anonymous_surface_is_exactly_the_allow_list()
    {
        var policy = _factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy;
        policy.Should().NotBeNull();
        policy!.Requirements.Should().Contain(r => r is DenyAnonymousAuthorizationRequirement);

        var anonymous = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Where(e => !IsHostOrStaticAsset(e))
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal(AnonymousAllowList, anonymous);
    }

    private static bool IsHostOrStaticAsset(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.RawText!.TrimStart('/').StartsWith("_framework", StringComparison.Ordinal)
        || endpoint.Metadata.Any(m => m.GetType().Name is "StaticAssetDescriptor" or "ComponentTypeMetadata" or "RootComponentMetadata");

    [Fact]
    public async Task BlazorFrameworkFile_Anonymous_IsNotGatedByAuth()
    {
        // Regression guard. The FallbackPolicy is applied by the authorization middleware to
        // requests that matched NO endpoint, and static files are middleware-served, not
        // endpoints — so with UseStaticFiles/UseBlazorFrameworkFiles placed after
        // UseAuthorization every asset answered 302-to-login and the browser rendered the Blazor
        // "unhandled error" shell instead of the app.
        //
        // Only the _framework path is asserted here: it is served out of the referenced Client
        // project's build output, so it resolves under the test host too. wwwroot assets like
        // /css/app.css cannot be checked at this tier because the factory points the content root
        // at a temp directory — StaticAssetsApiTests covers those against a real instance.
        var response = await _anonymous.GetAsync("/_framework/blazor.webassembly.js");

        response.StatusCode.Should().NotBe(HttpStatusCode.Found);
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BlazorShell_Anonymous_IsNotGatedByAuth()
    {
        // Gating the fallback file would make the login page itself unreachable. Under the test
        // host the content root is a temp directory with no index.html, so the interesting
        // assertion is the absence of an auth rejection — 404 means routing reached the fallback
        // and the file simply is not there; 401/302-to-login would mean the shell got gated.
        var response = await _anonymous.GetAsync("/login");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Found);
    }
}
