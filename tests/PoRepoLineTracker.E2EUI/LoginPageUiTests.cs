using FluentAssertions;
using Microsoft.Playwright;

namespace PoRepoLineTracker.E2EUI;

/// <summary>
/// C# Playwright UI E2E (replaces the legacy TypeScript suite). Covers the only
/// screen an unauthenticated visitor can reach, which is also the screen the deploy smoke test
/// lands on.
/// </summary>
[Collection(E2EUiCollection.Name)]
public sealed class LoginPageUiTests
{
    private readonly E2EUiFixture _fixture;

    public LoginPageUiTests(E2EUiFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task LoginPage_BootsCleanly_AndOffersASignInAction()
    {
        var page = await _fixture.OpenAsync(E2EUiFixture.Desktop, "/login");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);

        var errors = new List<string>();
        page.Console += (_, msg) =>
        {
            if (msg.Type == "error" && !msg.Text.Contains("SSL certificate error", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(msg.Text);
            }
        };

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 20000 });
        await page.WaitForSelectorAsync("button, a", new PageWaitForSelectorOptions { Timeout = 20000 });

        // The Po rule: the sign-in page shows only the title and ONE sign-in button.
        (await page.Locator(".po-header .po-header__title").InnerTextAsync()).Should().Contain("PoRepoLineTracker");
        (await page.Locator(".po-header a:not(.po-header__title), .po-header button").CountAsync())
            .Should().Be(0, "the header on the sign-in page carries only the title (no Sign in, no gear)");
        var actions = page.Locator("main button:visible, main a:visible");
        (await actions.CountAsync()).Should().Be(1, "the page offers exactly one action");
        (await actions.First.InnerTextAsync()).Should().Contain("Sign in with GitHub");

        errors.Should().BeEmpty("a clean boot logs no console errors");
    }

    [SkippableFact]
    public async Task ProtectedRoute_Anonymous_RedirectsToLoginWithoutRenderingRepositoryData()
    {
        var page = await _fixture.OpenAsync(E2EUiFixture.Desktop, "/repositories");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);

        await page.WaitForURLAsync("**/login", new PageWaitForURLOptions { Timeout = 20000 });

        page.Url.Should().Contain("/login", "an anonymous visitor must never reach the data screens");
    }
}
