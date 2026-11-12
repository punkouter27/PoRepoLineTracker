using FluentAssertions;
using Microsoft.Playwright;

namespace PoRepoLineTracker.E2EUI;

/// <summary>
/// Regression cover for the header's "Toggle navigation" button.
///
/// The existing suite asserted the sidebar's *resting* geometry at each breakpoint but never
/// clicked the toggle, so a toggle that silently did nothing looked perfectly healthy.
/// </summary>
[Collection(E2EUiCollection.Name)]
public sealed class SidebarToggleUiTests
{
    private readonly E2EUiFixture _fixture;

    public SidebarToggleUiTests(E2EUiFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The drawer toggle, located by the header region that holds it.
    ///
    /// <para>This was <c>button[title='Toggle navigation']</c>, a title no button in the app has
    /// ever carried — MainLayout renders "Hide navigation"/"Show navigation" depending on the
    /// current state. So all seven tests in this class died in <see cref="WaitForShellAsync"/>
    /// waiting 25s for an element that could not exist, and none of them had ever actually
    /// exercised the toggle they were written to cover.</para>
    ///
    /// <para>It went unnoticed because the whole tier skips when the Playwright browser build does
    /// not match the package — "64 skipped" reads as "app not running" rather than "64 tests are
    /// broken". Matching on the title is what made it fragile: the string is state-dependent by
    /// design. <c>.app-header__actions</c> holds exactly this one control, so it identifies the
    /// button without depending on which half of the toggle is showing.</para>
    /// </summary>
    private const string Toggle = ".app-header__actions button";

    /// <summary>
    /// Waits for the shell to exist, NOT for the sidebar to be visible. On a mobile viewport the
    /// drawer starts closed and therefore legitimately has zero width, which Playwright reports as
    /// hidden — waiting for visibility here would hang on correct behaviour.
    /// </summary>
    private static async Task WaitForShellAsync(IPage page)
    {
        await page.WaitForSelectorAsync(".rz-sidebar",
            new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached, Timeout = 25000 });
        await page.WaitForSelectorAsync(Toggle,
            new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 25000 });
    }

    private static Task<double> SidebarWidthAsync(IPage page) =>
        page.EvaluateAsync<double>("() => document.querySelector('.rz-sidebar').getBoundingClientRect().width");

    [SkippableFact]
    public async Task Desktop_ToggleCollapsesTheSidebar_SurvivesAResize_AndIsReversible()
    {
        var page = await _fixture.OpenAuthenticatedAsync(E2EUiFixture.Desktop, "/");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);
        await WaitForShellAsync(page);

        var before = await SidebarWidthAsync(page);
        before.Should().BeGreaterThan(100, "the sidebar starts expanded on a desktop viewport");

        await page.ClickAsync(Toggle);
        await page.WaitForTimeoutAsync(600); // width transition is 200ms

        var after = await SidebarWidthAsync(page);
        after.Should().BeLessThan(10, "clicking Toggle navigation must collapse the sidebar track to zero");

        // The whole point of collapsing on desktop: the body must reclaim the freed column
        // rather than leaving a dead 220px gutter.
        //
        // Asserting WIDTH as well as position is what makes this check worth having. An earlier
        // version checked only `left < 2` and passed against a layout that was badly broken —
        // .rz-body did start at x=0, but spanned just the 220px sidebar track, so the entire page
        // was crushed into a strip with the rest of the viewport left grey. Position alone cannot
        // tell "reclaimed the space" from "moved into the wrong column".
        var left = await page.EvaluateAsync<double>(
            "() => document.querySelector('.rz-body').getBoundingClientRect().left");
        left.Should().BeLessThan(2, "the content column must start at the viewport edge once the sidebar is collapsed");

        var bodyWidth = await page.EvaluateAsync<double>(
            "() => document.querySelector('.rz-body').getBoundingClientRect().width");
        var viewportWidth = await page.EvaluateAsync<double>("() => window.innerWidth");

        bodyWidth.Should().BeGreaterThan(viewportWidth - 4,
            "the content must span the FULL viewport once the sidebar track is collapsed, not just the freed track");

        // MainLayout drives _isMobile from <RadzenMediaQuery Change>, and that handler also sets
        // sidebarExpanded. If the handler fires on resizes that do NOT cross the 768px threshold,
        // it would overwrite the user's choice and the sidebar would spring back open — the
        // classic symptom of "the toggle doesn't stick". Resize well clear of the breakpoint.
        await page.SetViewportSizeAsync(1200, 900);
        await page.WaitForTimeoutAsync(600);

        (await SidebarWidthAsync(page))
            .Should().BeLessThan(10, "a resize that does not cross the breakpoint must not reopen the sidebar");

        await page.ClickAsync(Toggle);
        await page.WaitForTimeoutAsync(600);

        (await SidebarWidthAsync(page)).Should().BeGreaterThan(100, "a second click must restore the sidebar");
    }

    [SkippableFact]
    public async Task Mobile_ToggleOpensTheDrawerWithAScrim_AndTappingTheScrimClosesIt()
    {
        var page = await _fixture.OpenAuthenticatedAsync(E2EUiFixture.Mobile, "/");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);
        await WaitForShellAsync(page);

        // The drawer starts closed on a phone, so there is no scrim yet.
        (await page.Locator(".app-scrim").CountAsync())
            .Should().Be(0, "the drawer must start closed on a mobile viewport");

        await page.ClickAsync(Toggle);
        await page.WaitForTimeoutAsync(600);

        (await page.Locator(".app-scrim").CountAsync())
            .Should().Be(1, "opening the drawer must render the scrim that closes it again");

        var openClass = await page.EvaluateAsync<bool>(
            "() => document.querySelector('.rz-sidebar').classList.contains('app-sidebar--open')");
        openClass.Should().BeTrue("the drawer must carry app-sidebar--open once toggled");

        // Click near the RIGHT edge, not the scrim's centre. The scrim spans the full width and
        // the 220px drawer sits on top of its left portion, so the element's geometric centre on a
        // 390px viewport (x≈195) is underneath the drawer — Playwright's default click point would
        // be intercepted by it. That is correct layering, not a defect: the drawer must be above
        // the scrim. A user taps the part they can actually see.
        var viewport = page.ViewportSize!;
        await page.Mouse.ClickAsync(viewport.Width - 20, viewport.Height / 2);
        await page.WaitForTimeoutAsync(600);

        (await page.Locator(".app-scrim").CountAsync())
            .Should().Be(0, "tapping the visible part of the scrim must dismiss the drawer");
    }
}
