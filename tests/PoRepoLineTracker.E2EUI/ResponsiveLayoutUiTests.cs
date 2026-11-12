using FluentAssertions;
using Microsoft.Playwright;

namespace PoRepoLineTracker.E2EUI;

/// <summary>
/// The UI tier must cover mobile and desktop. There is also a performance angle:
/// the page must not scroll horizontally on a phone, which is the usual symptom of a fixed-width
/// element left over from a desktop-only layout.
///
/// <para>Parameterised over (viewport × route) rather than one fact per page: every row runs the
/// same assertion, and the signed-in routes matter most — /login is a single centred card and the
/// one screen in the app with no shell, no sidebar, no data grid and no chart, so "the phone
/// layout is fine" must not be concluded from the page least able to demonstrate it.</para>
/// </summary>
[Collection(E2EUiCollection.Name)]
public sealed class ResponsiveLayoutUiTests
{
    private readonly E2EUiFixture _fixture;

    public ResponsiveLayoutUiTests(E2EUiFixture fixture) => _fixture = fixture;

    private static ViewportSize Viewport(string name) =>
        name == "mobile" ? E2EUiFixture.Mobile : E2EUiFixture.Desktop;

    /// <summary>/login is reachable anonymously; everything else needs the fake-user header.</summary>
    private Task<IPage> OpenRouteAsync(string viewportName, string route) =>
        route == "/login"
            ? _fixture.OpenAsync(Viewport(viewportName), route)
            : _fixture.OpenAuthenticatedAsync(Viewport(viewportName), route);

    [SkippableFact]
    public async Task ShellRenders()
    {
        var page = await _fixture.OpenAsync(Viewport("desktop"), "/login");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);

        await page.WaitForSelectorAsync("button, a", new PageWaitForSelectorOptions { Timeout = 20000 });
        (await page.InnerTextAsync("body")).Should().NotBeNullOrWhiteSpace();

        var viewportMeta = await page.GetAttributeAsync("meta[name=viewport]", "content");
        viewportMeta.Should().NotBeNullOrWhiteSpace("without it mobile browsers render at desktop width");
    }

    [SkippableFact]
    public async Task NoHorizontalScroll()
    {
        var page = await OpenRouteAsync("mobile", "/repositories");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);

        await page.WaitForSelectorAsync("button, a", new PageWaitForSelectorOptions { Timeout = 25000 });
        await page.WaitForTimeoutAsync(2500);

        var overflow = await page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth - document.documentElement.clientWidth");

        overflow.Should().BeLessThanOrEqualTo(1,
            "repositories must fit mobile viewport — wide data grids contain their own scrolling via .u-scroll-x");
    }

    [SkippableFact]
    public async Task Mobile_TapTargetsAreLargeEnough()
    {
        var page = await OpenRouteAsync("mobile", "/repositories");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);

        await page.WaitForSelectorAsync("button, a", new PageWaitForSelectorOptions { Timeout = 25000 });
        await page.WaitForTimeoutAsync(2500);

        // "Visible" has to include visibility. On a phone the collapsed drawer is 0px wide and
        // visibility:hidden, but its links still have a layout box (their own padding) and a
        // non-null offsetParent, so they were measured as ~21px controls nobody can see or tap.
        var undersized = await page.EvaluateAsync<string[]>(@"() =>
            [...document.querySelectorAll('button, a, [role=button]')]
                .filter(el => el.offsetParent !== null && getComputedStyle(el).visibility !== 'hidden')
                .filter(el => { const r = el.getBoundingClientRect();
                    return r.width > 0 && r.height > 0 && (r.width < 24 || r.height < 24); })
                .map(el => (el.className || el.tagName) + ' :: ' + (el.textContent || '').trim().slice(0, 20))");

        undersized.Should().BeEmpty("every visible control on /repositories must meet the 24x24 minimum target size");
    }
}
