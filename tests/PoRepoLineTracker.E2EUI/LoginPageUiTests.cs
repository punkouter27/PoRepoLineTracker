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

        var signIn = page.GetByText("Sign in", new PageGetByTextOptions { Exact = false });
        (await signIn.CountAsync()).Should().BeGreaterThan(0);

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
