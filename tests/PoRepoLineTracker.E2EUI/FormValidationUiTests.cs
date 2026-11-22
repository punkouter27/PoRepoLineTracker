using FluentAssertions;
using Microsoft.Playwright;

namespace PoRepoLineTracker.E2EUI;

/// <summary>
/// FluentValidation shows its message inline under the field (no alert()), on the trimmed publish too.
/// </summary>
[Collection(E2EUiCollection.Name)]
public sealed class FormValidationUiTests
{
    private readonly E2EUiFixture _fixture;

    public FormValidationUiTests(E2EUiFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Delete_Account_Without_The_Word_Shows_The_Rule_Inline_And_Deletes_Nothing()
    {
        // Its own persona, so even a broken rule could only ever delete a throwaway account.
        var page = await _fixture.OpenAuthenticatedAsync(E2EUiFixture.Mobile, "/settings", fakeUser: "e2e-validation");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);
        var dialogs = 0;
        page.Dialog += (_, d) => { dialogs++; _ = d.DismissAsync(); };

        var box = page.Locator("#account-delete-confirm");
        await box.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        await box.FillAsync("delete");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Delete my account" }).ClickAsync();

        var message = page.Locator(".account-card__field .validation-message");
        await message.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        (await message.InnerTextAsync()).Should().Be("Type DELETE in capitals to confirm.");
        dialogs.Should().Be(0, "errors are inline, never alert()/confirm()");
        page.Url.Should().EndWith("/settings");
    }
}
