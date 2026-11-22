using FluentAssertions;
using Microsoft.Playwright;

namespace PoRepoLineTracker.E2EUI;

/// <summary>
/// The two pickers that used to be native selects are RadzenDropDowns over plain strings. Run against the trimmed
/// Release publish (scripts/run-e2e.ps1), this proves trimming did not break their binding: opening one lists the
/// options and choosing one acts on it.
/// </summary>
[Collection(E2EUiCollection.Name)]
public sealed class DropDownUiTests
{
    private readonly E2EUiFixture _fixture;

    public DropDownUiTests(E2EUiFixture fixture) => _fixture = fixture;

    private static async Task ChooseAsync(IPage page, string testId, string option)
    {
        // A view switch loads its data; a popup opened while that render lands can be closed by it, so open again
        // (at most three times) rather than click a closing list.
        var item = page.Locator(".rz-popup:visible .rz-dropdown-item", new PageLocatorOptions { HasTextString = option }).First;
        for (var attempt = 1; ; attempt++)
        {
            await page.Locator($"[data-testid={testId}]").ClickAsync();
            try
            {
                await item.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                return;
            }
            catch (TimeoutException) when (attempt < 3)
            {
                await page.Keyboard.PressAsync("Escape");
            }
        }
    }

    [SkippableFact]
    public async Task Insights_View_Picker_Switches_Between_Year_AllTime_And_CodeHealth()
    {
        Skip.IfNot(await E2ESeeder.EnsureSeededAsync(), "Seeding unavailable (needs a Development/Test host).");
        var page = await _fixture.OpenAuthenticatedAsync(E2EUiFixture.Desktop, "/insights");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);

        await page.Locator("[data-testid=insights-scope]").WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        (await page.Locator("[data-testid=insights-scope] input[aria-label='Insights view'], [data-testid=insights-scope][aria-label], [data-testid=insights-scope] [aria-label='Insights view']").CountAsync())
            .Should().BeGreaterThan(0, "the picker keeps its accessible name");

        // /insights opens on All time, so move away first.
        (await page.Locator("[data-testid=insights-scope] .rz-dropdown-label").InnerTextAsync()).Should().Be("All time");

        await ChooseAsync(page, "insights-scope", "Code health");
        await page.WaitForURLAsync("**/insights/health", new PageWaitForURLOptions { Timeout = 15000 });
        (await page.Locator("[data-testid=insights-scope] .rz-dropdown-label").InnerTextAsync()).Should().Be("Code health");

        await ChooseAsync(page, "insights-scope", "All time");
        await page.WaitForURLAsync("**/insights/all", new PageWaitForURLOptions { Timeout = 15000 });
        (await page.Locator("[data-testid=insights-scope] .rz-dropdown-label").InnerTextAsync()).Should().Be("All time");
    }

    [SkippableFact]
    public async Task Repositories_Sort_Picker_On_A_Phone_Changes_The_Sort()
    {
        Skip.IfNot(await E2ESeeder.EnsureSeededAsync(), "Seeding unavailable (needs a Development/Test host).");
        var page = await _fixture.OpenAuthenticatedAsync(E2EUiFixture.Mobile, "/repositories");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);

        var picker = page.Locator("[data-testid=repositories-sort]");
        await picker.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });

        await ChooseAsync(page, "repositories-sort", "Total lines");
        (await picker.Locator(".rz-dropdown-label").InnerTextAsync()).Should().Be("Total lines");

        await ChooseAsync(page, "repositories-sort", "Repository");
        (await picker.Locator(".rz-dropdown-label").InnerTextAsync()).Should().Be("Repository");
    }
}
