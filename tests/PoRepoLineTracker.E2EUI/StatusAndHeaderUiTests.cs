using FluentAssertions;
using Microsoft.Playwright;

namespace PoRepoLineTracker.E2EUI;

/// <summary>
/// The fleet /diag standard (2026-11-17) and the header Settings control, at both viewports.
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

        var text = await page.InnerTextAsync("main");
        text.Should().Contain("API").And.Contain("Data").And.Contain("Services");
        text.Should().Contain("Azure Table Storage").And.Contain("GitHub API");
        (await page.Locator(".diag-refresh").IsEnabledAsync()).Should().BeTrue("Refresh is usable once the checks are back");

        var overflow = await page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
        overflow.Should().BeLessThanOrEqualTo(1);
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
}
