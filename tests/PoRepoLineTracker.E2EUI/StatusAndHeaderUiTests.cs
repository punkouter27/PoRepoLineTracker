using FluentAssertions;
using Microsoft.Playwright;

namespace PoRepoLineTracker.E2EUI;

/// <summary>
/// The Po /diag contract (System status) and the header Settings control, at both viewports.
/// </summary>
[Collection(E2EUiCollection.Name)]
public sealed class StatusAndHeaderUiTests
{
    private readonly E2EUiFixture _fixture;

    public StatusAndHeaderUiTests(E2EUiFixture fixture) => _fixture = fixture;

    private static ViewportSize Viewport(string name) =>
        name == "mobile" ? E2EUiFixture.Mobile : E2EUiFixture.Desktop;

    /// <summary>Signed-out: the status banner and all three sections render, nothing overflows, no console errors.</summary>
    [SkippableTheory]
    [InlineData("desktop")]
    [InlineData("mobile")]
    public async Task Diag_Renders_SignedOut_With_Zero_Console_Errors(string viewport)
    {
        var page = await _fixture.OpenAsync(Viewport(viewport), "/diag");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);

        await page.WaitForSelectorAsync(".diag-banner[data-status]", new PageWaitForSelectorOptions { Timeout = 30000 });

        (await page.TitleAsync()).Should().Be("System status");
        (await page.InnerTextAsync("h1")).Should().Be("System status");
        var text = await page.InnerTextAsync("main");
        text.Should().Contain("API").And.Contain("Data").And.Contain("Services");
        text.Should().Contain("Azure Table Storage").And.Contain("GitHub API");
        (await page.Locator(".diag-refresh").IsEnabledAsync()).Should().BeTrue("Refresh is usable once the checks are back");

        var overflow = await page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
        overflow.Should().BeLessThanOrEqualTo(1);

        // Signed out there is no navigation (every destination would bounce to the sign-in page).
        (await page.Locator(".rz-sidebar, .bottom-nav, nav").CountAsync())
            .Should().Be(0, "navigation is hidden while signed out");
        (await page.Locator("[data-testid=header-sign-in]").CountAsync()).Should().Be(1);
    }

    /// <summary>Radzen's theme sheet loads before the app's own sheets, so the app's rules win on equal specificity.</summary>
    [SkippableFact]
    public async Task Radzen_Css_Loads_Before_The_App_Css()
    {
        var page = await _fixture.OpenAsync(E2EUiFixture.Desktop, "/diag");
        await using var _ = page.Context;

        var sheets = await page.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('link[rel=stylesheet]')].map(l => new URL(l.href).pathname)");
        var radzen = Array.FindLastIndex(sheets, s => s.StartsWith("/_content/Radzen.Blazor/css/", StringComparison.Ordinal));
        var app = Array.FindIndex(sheets, s => s is "/css/po-tokens.css" or "/css/app.css" || s.EndsWith(".styles.css", StringComparison.Ordinal));
        radzen.Should().BeGreaterThanOrEqualTo(0, "a Radzen theme sheet is linked");
        app.Should().BeGreaterThan(radzen, $"every Radzen sheet comes before the app's sheets: {string.Join(", ", sheets)}");
    }

    /// <summary>Settings is reachable from the header's upper-right, not only from the sidebar.</summary>
    [SkippableTheory]
    [InlineData("desktop")]
    [InlineData("mobile")]
    public async Task Header_Settings_Link_Is_Visible_UpperRight_And_Opens_Settings(string viewport)
    {
        var size = Viewport(viewport);
        var page = await _fixture.OpenAuthenticatedAsync(size, "/repositories");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);

        var link = page.Locator("[data-testid=header-settings]");
        await link.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });

        var box = await link.BoundingBoxAsync();
        box.Should().NotBeNull();
        box!.Y.Should().BeLessThan(80, "it sits in the header bar");
        (box.X + box.Width).Should().BeLessThanOrEqualTo(size.Width, "fully on screen");
        box.X.Should().BeGreaterThan(size.Width / 2f, "upper-right, with the session controls");

        await link.ClickAsync();
        await page.WaitForURLAsync("**/settings", new PageWaitForURLOptions { Timeout = 15000 });
        await page.WaitForSelectorAsync("h1:has-text('Settings')", new PageWaitForSelectorOptions { Timeout = 15000 });
    }

    /// <summary>
    /// A test sign-in (dev persona whose name is an email, so it carries the email claim like a GitHub account that
    /// shares its email): the header shows that email top-right, the title top-left, and the version stamp bottom-right.
    /// </summary>
    [SkippableTheory]
    [InlineData("desktop")]
    [InlineData("mobile")]
    public async Task Header_Shows_The_Signed_In_Email_UpperRight(string viewport)
    {
        const string email = "e2e-header@example.com";
        var size = Viewport(viewport);
        var page = await _fixture.OpenAuthenticatedAsync(size, "/repositories", fakeUser: email);
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);

        var shown = page.Locator(".po-header .po-header__email");
        await shown.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        (await shown.InnerTextAsync()).Trim().Should().Be(email);
        (await shown.GetAttributeAsync("title")).Should().Be(email, "the full email stays available when it is cut short on a phone");

        var box = (await shown.BoundingBoxAsync())!;
        box.Y.Should().BeLessThan(56, "it sits in the header bar");
        box.X.Should().BeGreaterThan(size.Width / 2f, "upper-right");
        (box.X + box.Width).Should().BeLessThanOrEqualTo(size.Width);

        var title = (await page.Locator(".po-header__title").BoundingBoxAsync())!;
        title.X.Should().BeLessThan(40, "the app title is top-left");

        var stamp = page.Locator(".po-version");
        (await stamp.InnerTextAsync()).Should().MatchRegex(@"^v\d{6}$");
    }
}
