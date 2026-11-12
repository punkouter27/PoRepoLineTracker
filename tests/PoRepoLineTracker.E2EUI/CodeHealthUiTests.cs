using FluentAssertions;
using Microsoft.Playwright;

namespace PoRepoLineTracker.E2EUI;

/// <summary>
/// The code-health card on the repository detail page.
///
/// <para>The card is deliberately inert until asked: the report walks the whole HEAD tree and runs
/// every source file through the analyser, which is seconds of work and not what someone opening a
/// repository came for. These assert that it costs nothing on load, that the button drives it, and
/// that a repository with no clone on disk lands in an explained state rather than a blank card or
/// a wall of zeroes — the synthetic history the suite seeds has no git objects behind it, so that
/// is exactly the path a test account takes.</para>
/// </summary>
[Collection(E2EUiCollection.Name)]
public sealed class CodeHealthUiTests
{
    private readonly E2EUiFixture _fixture;

    public CodeHealthUiTests(E2EUiFixture fixture) => _fixture = fixture;

    private async Task<IPage> OpenSeededRepositoryAsync()
    {
        Skip.IfNot(await E2ESeeder.EnsureSeededAsync(),
            $"Could not seed a repository at {E2EUiFixture.BaseUrl} — is the app running in Development?");

        var page = await _fixture.OpenAuthenticatedAsync(E2EUiFixture.Desktop, "/");
        await page.Locator(".rp-grid:visible, .rp-card-list:visible, .rz-datatable:visible, .onboarding__card:visible")
            .First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 25000 });

        // Straight to the seeded repository's detail page via its link/row in the grid.
        var link = page.Locator("a[href^='/repositories/']:visible, .rz-data-row:visible").First;
        await link.ClickAsync();
        await page.WaitForURLAsync("**/repositories/**", new PageWaitForURLOptions { Timeout = 20000 });

        // The card lives in the third tab; a hidden panel never satisfies a visibility wait.
        await page.Locator(".rz-tabview-nav li", new PageLocatorOptions { HasText = "Code Health" }).ClickAsync();
        await page.WaitForSelectorAsync(".health", new PageWaitForSelectorOptions { Timeout = 25000 });
        return page;
    }

    [SkippableFact]
    public async Task Card_IsPresentButUnloaded_UntilAsked()
    {
        var page = await OpenSeededRepositoryAsync();
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);

        // Chrome and the trigger are there...
        (await page.Locator(".health__card").CountAsync()).Should().Be(1);
        (await page.InnerTextAsync(".health")).Should().Contain("Code health");

        // ...and nothing has been computed, so no grade is on screen.
        (await page.Locator(".health__grade").CountAsync())
            .Should().Be(0, "the report must not run on page load");
    }

    [SkippableFact]
    public async Task RunningIt_ReachesAnExplainedState_NotABlankCard()
    {
        var page = await OpenSeededRepositoryAsync();
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);
        guard.AllowHttp(System.Net.HttpStatusCode.NotFound, "/api/code-health/");

        await page.Locator(".health button").First.ClickAsync();

        // Either a real report or an explanation — never an empty card. The seeded repository has
        // commit rows but no git objects on disk, so it takes the explained path.
        await page.WaitForSelectorAsync(".health__grade, .health__message",
            new PageWaitForSelectorOptions { Timeout = 30000 });

        var text = await page.InnerTextAsync(".health");
        text.Trim().Should().NotBeNullOrEmpty();

        if (await page.Locator(".health__message").CountAsync() > 0)
        {
            (await page.InnerTextAsync(".health__message")).Trim()
                .Should().NotBeNullOrEmpty("an empty explanation is the same as a blank card");
        }
        else
        {
            // A real report must show its breakdown, not just a number — an unexplained score
            // invites being read as more precise than it is.
            (await page.Locator(".health__factor").CountAsync()).Should().Be(6);
            (await page.InnerTextAsync(".health__caveat")).Should().Contain("approximation");
        }
    }
}
