using FluentAssertions;
using Microsoft.Playwright;

namespace PoRepoLineTracker.E2EUI;

/// <summary>
/// Inline styles are forbidden, so the rules that used to sit on <c>style=</c> must now
/// arrive from a scoped <c>.razor.css</c>. Asserting on the *computed* style is what makes the
/// extraction verifiable: a class that never matched (a missing <c>::deep</c>, a wrong anchor)
/// silently drops the styling, and only the computed value catches it. (The bundle's full
/// contents are asserted per component in the E2EAPI tier's StaticAssetsApiTests.)
/// </summary>
[Collection(E2EUiCollection.Name)]
public sealed class ScopedCssUiTests
{
    private readonly E2EUiFixture _fixture;

    public ScopedCssUiTests(E2EUiFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task LoginScreen_IsStyledByScopedCss_WithNoInlineStylesOfItsOwn()
    {
        var page = await _fixture.OpenAsync(E2EUiFixture.Desktop, "/login");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);
        await page.WaitForSelectorAsync(".login-card", new PageWaitForSelectorOptions { Timeout = 20000 });

        // The scoped rule must actually reach the Radzen-rendered card — a rule that never
        // matched leaves the browser default of "none".
        var maxWidth = await page.EvaluateAsync<string>(
            "() => getComputedStyle(document.querySelector('.login-card')).maxWidth");

        maxWidth.Should().Be("420px", "the extracted rule must still reach the Radzen-rendered card");

        // The rule itself, asserted against the rendered DOM. Radzen sets inline styles of its
        // own on the elements it generates (`rz-*` classes, and `rzi` on icons), so this counts
        // only the elements our own markup owns.
        var inlineStyled = await page.EvaluateAsync<string[]>(@"() =>
            [...document.querySelectorAll('.login-canvas [style]')]
                .filter(el => ![...el.classList].some(c => c.startsWith('rz-') || c === 'rzi'))
                .map(el => el.tagName + '.' + el.className)");

        inlineStyled.Should().BeEmpty("every style on our own markup must come from Login.razor.css");
    }
}
