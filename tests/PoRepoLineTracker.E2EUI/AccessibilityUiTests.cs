using FluentAssertions;
using Microsoft.Playwright;

namespace PoRepoLineTracker.E2EUI;

/// <summary>
/// WCAG 2.2 AA on interactive elements. These are the machine-checkable subset
/// (accessible names, document language, focus order, contrast-independent structure); they do
/// not replace a manual audit, but they catch the regressions that reach production. The static
/// rules all scan the same rendered page, so they run as one test.
/// </summary>
[Collection(E2EUiCollection.Name)]
public sealed class AccessibilityUiTests
{
    private readonly E2EUiFixture _fixture;

    public AccessibilityUiTests(E2EUiFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task LoginPage_PassesTheMachineCheckableWcagRules()
    {
        var page = await _fixture.OpenAsync(E2EUiFixture.Desktop, "/login");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);
        await page.WaitForSelectorAsync("button, a", new PageWaitForSelectorOptions { Timeout = 20000 });

        // WCAG 3.1.1 — screen readers pick pronunciation from this.
        (await page.GetAttributeAsync("html", "lang")).Should().NotBeNullOrWhiteSpace();

        // WCAG 2.4.2 — the title is how a user distinguishes tabs.
        (await page.TitleAsync()).Should().NotBeNullOrWhiteSpace();

        // WCAG 4.1.2 — an icon-only button with no aria-label announces as "button".
        var unnamedButtons = await page.EvaluateAsync<int>(@"() =>
            [...document.querySelectorAll('button')]
                .filter(el => el.offsetParent !== null)
                .filter(el => !(el.innerText || '').trim()
                           && !el.getAttribute('aria-label')
                           && !el.getAttribute('aria-labelledby')
                           && !el.getAttribute('title'))
                .length");
        unnamedButtons.Should().Be(0, "every visible button must announce a name");

        var unnamedLinks = await page.EvaluateAsync<int>(@"() =>
            [...document.querySelectorAll('a[href]')]
                .filter(el => el.offsetParent !== null)
                .filter(el => !(el.innerText || '').trim()
                           && !el.getAttribute('aria-label')
                           && !el.getAttribute('title'))
                .length");
        unnamedLinks.Should().Be(0, "every visible link must announce a name");

        // WCAG 1.1.1 — decorative images must still carry an explicit empty alt.
        var missingAlt = await page.EvaluateAsync<int>(
            "() => [...document.querySelectorAll('img')].filter(el => el.getAttribute('alt') === null).length");
        missingAlt.Should().Be(0, "every image needs an alt attribute, even an empty one");

        // A positive tabindex reorders the tab sequence away from DOM order (WCAG 2.4.3).
        var positiveTabIndex = await page.EvaluateAsync<int>(
            "() => [...document.querySelectorAll('[tabindex]')].filter(el => Number(el.getAttribute('tabindex')) > 0).length");
        positiveTabIndex.Should().Be(0, "no element may opt out of DOM-order tabbing");

        // WCAG 1.3.1 / 3.3.2 — an unlabelled field announces only its type.
        var unlabelled = await page.EvaluateAsync<int>(@"() =>
            [...document.querySelectorAll('input:not([type=hidden]), select, textarea')]
                .filter(el => el.offsetParent !== null)
                .filter(el => !el.getAttribute('aria-label')
                           && !el.getAttribute('aria-labelledby')
                           && !el.getAttribute('placeholder')
                           && !(el.id && document.querySelector(`label[for='${el.id}']`))
                           && !el.closest('label'))
                .length");
        unlabelled.Should().Be(0, "every visible form field must be labelled");
    }

    [SkippableFact]
    public async Task InteractiveElements_AreKeyboardReachable()
    {
        // WCAG 2.1.1 — pressing Tab must move focus onto a real control.
        var page = await _fixture.OpenAsync(E2EUiFixture.Desktop, "/login");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);
        await page.WaitForSelectorAsync("button, a", new PageWaitForSelectorOptions { Timeout = 20000 });

        await page.Keyboard.PressAsync("Tab");
        var focused = await page.EvaluateAsync<string>("() => document.activeElement?.tagName ?? ''");

        focused.Should().NotBe("BODY", "focus must land on an interactive element");
    }

    /// <summary>
    /// Document structure, on every signed-in route.
    ///
    /// <para>A runtime sweep found all four of these missing on all six routes at once: no page
    /// declared an &lt;h1&gt; (the header's brand wordmark was an &lt;h5&gt;, so every outline ran
    /// H5 → H3 → H6 with the brand outranking the page), no page had a &lt;main&gt; landmark, and
    /// every route shared the single title "PoRepoLineTracker" — so tabs, history and bookmarks
    /// were indistinguishable. None of it is visible on screen, which is precisely why it stayed
    /// broken and why it is asserted here rather than left to review.</para>
    /// </summary>
    [SkippableTheory]
    [InlineData("/login", "Sign in")]
    [InlineData("/", "Repositories")]
    public async Task EveryRoute_HasOneH1_AMainLandmark_AndItsOwnTitle(string route, string expectedInTitle)
    {
        var page = await _fixture.OpenAuthenticatedAsync(E2EUiFixture.Desktop, route);
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);
        await page.WaitForSelectorAsync("h1", new PageWaitForSelectorOptions { Timeout = 25000 });

        var h1Count = await page.Locator("h1").CountAsync();
        h1Count.Should().Be(1, $"{route} must declare exactly one top-level heading");

        var mainCount = await page.Locator("main, [role=main]").CountAsync();
        mainCount.Should().Be(1, $"{route} needs a main landmark for the skip link to target");

        var title = await page.TitleAsync();
        title.Should().Contain(expectedInTitle, $"{route} must be distinguishable in a tab strip");

        // The heading that opens the document must be the h1 — no lower-ranked heading above it.
        var firstHeading = await page.EvaluateAsync<string>(
            "() => document.querySelector('h1,h2,h3,h4,h5,h6')?.tagName ?? ''");
        firstHeading.Should().Be("H1", "the first heading in the document must not outrank the page title");
    }

    [SkippableFact]
    public async Task SkipLink_IsFirstInTabOrder_AndMovesFocusToMain()
    {
        var page = await _fixture.OpenAuthenticatedAsync(E2EUiFixture.Desktop, "/insights");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);
        await page.WaitForSelectorAsync("h1", new PageWaitForSelectorOptions { Timeout = 25000 });

        // Focus starts at the document, so one Tab must reach the skip link — anything else means
        // a keyboard user tabs the whole sidebar before reaching content, on every navigation.
        await page.Keyboard.PressAsync("Tab");
        var focusedHref = await page.EvaluateAsync<string>(
            "() => document.activeElement?.getAttribute('href') ?? ''");
        focusedHref.Should().Be("#main-content");

        // Off-canvas until focused, and on-screen once it is — a skip link nobody can see is one
        // nobody can use.
        var visibleLeft = await page.EvaluateAsync<double>(
            "() => document.querySelector('.app-skip-link').getBoundingClientRect().left");
        visibleLeft.Should().BeGreaterThanOrEqualTo(0, "the focused skip link must be on screen");

        await page.Keyboard.PressAsync("Enter");
        var focusedId = await page.EvaluateAsync<string>("() => document.activeElement?.id ?? ''");
        focusedId.Should().Be("main-content", "following the link must MOVE focus, not merely scroll");
    }
}
