using System.Net;
using FluentAssertions;

namespace PoRepoLineTracker.E2EAPI;

/// <summary>
/// Regression guard for the middleware ordering that the FallbackPolicy made
/// load-bearing: the authorization middleware applies the fallback to requests that matched no
/// endpoint, and static files are served by middleware rather than endpoints. With
/// UseStaticFiles/UseBlazorFrameworkFiles registered after UseAuthorization, every asset answered
/// 302-to-login and the browser rendered Blazor's "unhandled error" shell instead of the app.
///
/// This lives in the E2E tier because it needs a real content root — the integration test host
/// points the content root at a temp directory and has no wwwroot to serve.
/// </summary>
public sealed class StaticAssetsApiTests
{
    [SkippableFact]
    public async Task StaticAssets_And_Stylesheets_Anonymous_AreServed()
    {
        var shellResponse = await E2EApiClient.GetAsync("/");
        shellResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var jsResponse = await E2EApiClient.GetAsync("/_framework/blazor.webassembly.js");
        jsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var cssResponse = await E2EApiClient.GetAsync("/css/app.css");
        cssResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        cssResponse.Content.Headers.ContentType?.MediaType.Should().Be("text/css");
    }

    /// <summary>
    /// One representative class per extracted component. A .razor.css that fails to compile into
    /// the bundle takes its component's styling with it and produces no build error, so the
    /// bundle's contents are the only cheap proof that the extraction is wired up.
    /// </summary>
    private static readonly (string CssClass, string Source)[] ExtractedComponentClasses =
    [
        ("login-cta-btn", "Login.razor.css"),
        ("nav-section-label", "NavMenu.razor.css"),
        ("login-display__avatar", "Auth/LoginDisplay.razor.css"),
        ("add-repo-progress-message", "AddRepository.razor.css"),
        ("brand-wordmark", "MainLayout.razor.css"),
        ("ext-code", "Settings.razor.css"),
        // MobileRepoCard.razor(.css) was deleted: no page ever referenced the component, and the
        // `.mobile-only` / `.desktop-only` classes written to switch it in were unused too.
        // Radzen's own responsive DataGrid covers the case it was built for.
        ("xc-env-card", "Operator/Diagnostics.razor.css"),
        ("arc-legend__swatch", "Charts/AllReposComparisonChart.razor.css"),
        ("chart-card__title", "ChartCard.razor.css"),
        ("page-hero__actions", "PageHero.razor.css"),
        ("cc-bar", "Charts/ContributorChart.razor.css"),
        ("rd-ext-bar", "RepositoryDetail.razor.css"),
        ("rp-lines-value", "Repositories.razor.css"),
        ("rp-stat-spark", "PortfolioStatTiles.razor.css"),
        ("status-cell", "AnalysisStatusCell.razor.css"),
        ("rp-live-dot", "RepositoriesGrid.razor.css"),
        ("digest__stat-value", "DigestBanner.razor.css"),
        ("health__factor-measure", "CodeHealthCard.razor.css"),
    ];

    [SkippableFact]
    public async Task ScopedComponentStylesheet_IsServed_Scoped_AndContainsEveryExtractedRule()
    {
        // The bundle Blazor generates from the .razor.css files. If CSS isolation is
        // not wired up this 404s and every extracted component style silently disappears.
        var response = await E2EApiClient.GetAsync("/PoRepoLineTracker.Client.styles.css");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var css = await response.Content.ReadAsStringAsync();

        // Every rule in the bundle should be scoped (b-xxxxxxxxxx). A bundle with no scope
        // attributes would mean isolation silently degraded to global CSS.
        css.Should().MatchRegex(@"\[b-[a-z0-9]+\]");

        foreach (var (cssClass, source) in ExtractedComponentClasses)
        {
            css.Should().Contain(cssClass, $"{source} must be part of the scoped bundle");
        }
    }
}
