using FluentAssertions;
using Microsoft.Playwright;

namespace PoRepoLineTracker.E2EUI;

/// <summary>
/// System-aware light/dark theming, asserted from the rendered result: both schemes must
/// paint, and they must paint differently. (The data-theme boot attribute and the dark chart
/// surface are covered in <see cref="ChartAndShellUiTests"/> against the signed-in pages.)
/// </summary>
[Collection(E2EUiCollection.Name)]
public sealed class ThemeAndAssetsUiTests
{
    private readonly E2EUiFixture _fixture;

    public ThemeAndAssetsUiTests(E2EUiFixture fixture) => _fixture = fixture;

    private async Task<IPage> OpenWithSchemeAsync(string scheme)
    {
        Skip.If(_fixture.Browser is null, "Playwright browser unavailable — run playwright.ps1 install.");

        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = E2EUiFixture.Desktop,
            IgnoreHTTPSErrors = true,
            ColorScheme = scheme == "dark" ? ColorScheme.Dark : ColorScheme.Light
        });
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync(E2EUiFixture.BaseUrl.TrimEnd('/') + "/login", new PageGotoOptions { Timeout = 10000 });
        }
        catch (PlaywrightException ex)
        {
            await context.DisposeAsync();
            throw new SkipException($"No app instance reachable at {E2EUiFixture.BaseUrl} ({ex.Message}).");
        }
        return page;
    }

    private static Task<string> BodyBackgroundAsync(IPage page) => page.EvaluateAsync<string>(
        "() => getComputedStyle(document.documentElement).backgroundColor");

    [SkippableFact]
    public async Task Theme_RendersBothSchemes_AndRespondsToTheSystemPreference()
    {
        // The app must follow the OS preference, not pick one and ignore it.
        var light = await OpenWithSchemeAsync("light");
        await using var _ = light.Context;
        await using var lightGuard = PlaywrightErrorGuard.Watch(light);
        await light.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 20000 });
        var lightBackground = await BodyBackgroundAsync(light);
        lightBackground.Should().NotBeNullOrWhiteSpace();

        var dark = await OpenWithSchemeAsync("dark");
        await using var __ = dark.Context;
        await using var darkGuard = PlaywrightErrorGuard.Watch(dark);
        await dark.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 20000 });
        var darkBackground = await BodyBackgroundAsync(dark);
        darkBackground.Should().NotBeNullOrWhiteSpace();

        darkBackground.Should().NotBe(lightBackground,
            "a system-aware theme must render differently under a dark preference");
    }
}
