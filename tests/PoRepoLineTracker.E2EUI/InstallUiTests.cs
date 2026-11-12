using FluentAssertions;
using Microsoft.Playwright;

namespace PoRepoLineTracker.E2EUI;

/// <summary>
/// The installability plumbing, asserted in a real browser.
///
/// <para>None of it fails loudly. A service worker that fails to register produces no error
/// anywhere a build or a unit test can see it; the only symptom is that the install button never
/// appears. Same for a manifest that is present but not linked, or an icon a pixel too small.</para>
/// </summary>
[Collection(E2EUiCollection.Name)]
public sealed class InstallUiTests
{
    private readonly E2EUiFixture _fixture;

    public InstallUiTests(E2EUiFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The three things a browser needs before it will ever consider offering an install: a linked
    /// manifest it can parse, a registered service worker, and an icon of at least 192px. None of
    /// them fails loudly — a missing one just means the prompt silently never fires.
    /// </summary>
    [SkippableFact]
    public async Task AppShell_IsInstallable_ManifestLinkedAndWorkerRegistered()
    {
        var page = await _fixture.OpenAsync(E2EUiFixture.Desktop, "/login");
        await using var _ = page.Context;
        await using var guard = PlaywrightErrorGuard.Watch(page);
        await page.WaitForSelectorAsync("button, a", new PageWaitForSelectorOptions { Timeout = 20000 });

        var manifestHref = await page.GetAttributeAsync("link[rel=manifest]", "href");
        manifestHref.Should().NotBeNullOrWhiteSpace("without a linked manifest the app is never installable");

        var manifest = await page.EvaluateAsync<string>(
            "async href => (await fetch(href)).text()", manifestHref);

        manifest.Should().Contain("\"standalone\"", "an installed window must not be a browser tab");
        manifest.Should().Contain("192x192", "browsers require an icon of at least 192px to install");

        var registered = await page.WaitForFunctionAsync(
            "async () => !!(await navigator.serviceWorker.getRegistration())",
            null,
            new PageWaitForFunctionOptions { Timeout = 20000 });

        registered.Should().NotBeNull();

        var cacheKeys = await page.EvaluateAsync<string[]>("async () => await caches.keys()");
        cacheKeys.Should().NotContain(key => key.StartsWith("porepolinetracker-cache-"),
            "the development worker is a no-op — a populated cache here means the published one is being served");
    }
}
