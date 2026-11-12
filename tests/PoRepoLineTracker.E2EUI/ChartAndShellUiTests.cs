using FluentAssertions;
using Microsoft.Playwright;

namespace PoRepoLineTracker.E2EUI;

/// <summary>
/// Regression cover for the shell-layout and repo-detail defects fixed in the mobile-first
/// pass. The chart-axes / chart-theme tests that used to live here were dropped when the
/// portfolio trend chart was removed from the Code Health view — what is left is the shell layout
/// (sidebar beside body on desktop, drawer on mobile) and the repo detail page's panel set.
///
/// Each one asserts on the rendered result rather than on the CSS text: a rule that never matched
/// (the `::deep` shell selectors), a token that never resolved, or an axis that drew no labels all
/// look perfectly correct in the stylesheet and only fail in the DOM.
/// </summary>
[Collection(E2EUiCollection.Name)]
public sealed class ChartAndShellUiTests
{
    private readonly E2EUiFixture _fixture;

    public ChartAndShellUiTests(E2EUiFixture fixture) => _fixture = fixture;

    /// <summary>Waits for the repositories page to finish its initial data load.</summary>
    private static async Task WaitForRepositoriesAsync(IPage page)
    {
        await page.Locator(".rp-grid:visible, .rp-card-list:visible, .chart-card:visible, .onboarding__card:visible")
            .First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 25000 });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
            new PageWaitForLoadStateOptions { Timeout = 25000 });
    }

    [SkippableFact]
    public async Task ShellGrid_PutsSidebarBesideBody_AndAdaptsToMobile()
    {
        var desktopPage = await _fixture.OpenAuthenticatedAsync(E2EUiFixture.Desktop, "/repositories");
        await using var _ = desktopPage.Context;
        await using var desktopGuard = PlaywrightErrorGuard.Watch(desktopPage);
        await WaitForRepositoriesAsync(desktopPage);

        var layout = await desktopPage.EvaluateAsync<string>(
            "() => getComputedStyle(document.querySelector('.rz-layout')).display");
        var sidebarRight = await desktopPage.EvaluateAsync<int>(
            "() => Math.round(document.querySelector('.rz-sidebar').getBoundingClientRect().right)");
        var bodyLeftDesktop = await desktopPage.EvaluateAsync<int>(
            "() => Math.round(document.querySelector('.rz-body').getBoundingClientRect().left)");

        layout.Should().Be("grid");
        bodyLeftDesktop.Should().BeGreaterThanOrEqualTo(sidebarRight - 1,
            "the body must sit to the right of the sidebar, not underneath it");

        var mobilePage = await _fixture.OpenAuthenticatedAsync(E2EUiFixture.Mobile, "/repositories");
        await using var __ = mobilePage.Context;
        await using var mobileGuard = PlaywrightErrorGuard.Watch(mobilePage);
        await WaitForRepositoriesAsync(mobilePage);

        var bodyLeftMobile = await mobilePage.EvaluateAsync<int>(
            "() => Math.round(document.querySelector('.rz-body').getBoundingClientRect().left)");
        bodyLeftMobile.Should().BeLessThanOrEqualTo(1,
            "the content column must start at the left edge once the sidebar becomes a drawer");
    }

    // ─── Repository detail ───────────────────────────────────────────────────────

    private async Task<IPage> OpenFirstRepositoryAsync(ViewportSize viewport)
    {
        Skip.IfNot(await E2ESeeder.EnsureSeededAsync(),
            $"Could not seed chart data at {E2EUiFixture.BaseUrl} — is the app running in Development?");

        var page = await _fixture.OpenAuthenticatedAsync(viewport, "/repositories");
        await WaitForRepositoriesAsync(page);

        var row = page.Locator(".rz-data-row:visible, .rp-repo-card__link:visible").First;
        await row.WaitForAsync(new LocatorWaitForOptions { Timeout = 25000 });

        await row.ClickAsync();
        await page.WaitForURLAsync("**/repositories/**", new PageWaitForURLOptions { Timeout = 20000 });
        await page.WaitForSelectorAsync(".chart-card", new PageWaitForSelectorOptions { Timeout = 25000 });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 25000 });
        return page;
    }

    [SkippableFact]
    public async Task Detail_RendersPanels_AndFitsMobileViewport()
    {
        var desktopPage = await OpenFirstRepositoryAsync(E2EUiFixture.Desktop);
        await using var _ = desktopPage.Context;
        await using var desktopGuard = PlaywrightErrorGuard.Watch(desktopPage);

        // Only the line-count chart is wrapped in a ChartCard; the rest moved into tabs after
        // the trend chart was removed.
        var cardTitles = await desktopPage.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('.chart-card__title')].map(t => t.textContent.trim())");
        cardTitles.Should().Contain("Line Count History");

        var tabTitles = await desktopPage.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('.rz-tabview-title')].map(t => t.textContent.trim())");
        tabTitles.Should().Contain("Activity & Authorship");
        tabTitles.Should().Contain("Composition");
        tabTitles.Should().Contain("Code Health");

        await desktopPage.WaitForSelectorAsync(".rz-chart svg", new PageWaitForSelectorOptions { Timeout = 25000 });

        var text = await desktopPage.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('.rz-chart text')].map(t => t.textContent.trim())");

        text.Should().Contain("Date");
        text.Should().Contain("Lines of Code");

        var mobilePage = await OpenFirstRepositoryAsync(E2EUiFixture.Mobile);
        await using var __ = mobilePage.Context;
        await using var mobileGuard = PlaywrightErrorGuard.Watch(mobilePage);

        var overflow = await mobilePage.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth - document.documentElement.clientWidth");

        overflow.Should().BeLessThanOrEqualTo(1,
            "the contributor grid and chart panels must fit the phone viewport");
    }
}
