using Microsoft.Playwright;

namespace PoRepoLineTracker.E2EUI;

/// <summary>
/// One Playwright browser shared by every UI E2E class. Launching a browser per class
/// dominates the suite's wall-clock, and none of these tests mutate global browser state — each
/// gets its own context and page.
///
/// Both "Playwright browsers are not installed" and "no app instance is reachable" are reported
/// as skips, never failures: this tier runs locally and against the Test environment, and CI does
/// not execute tests.
/// </summary>
public sealed class E2EUiFixture : IAsyncLifetime
{
    /// <summary>Desktop and mobile viewports — the UI tier must cover both.</summary>
    public static readonly ViewportSize Desktop = new() { Width = 1440, Height = 900 };
    public static readonly ViewportSize Mobile = new() { Width = 390, Height = 844 };

    /// <summary>
    /// HTTPS by default. SecurityHeadersMiddleware sends <c>upgrade-insecure-requests</c>, which
    /// makes the browser re-request every asset over TLS — so a plain-http target cannot load the
    /// Blazor framework files and the app renders its "unhandled error" shell instead. Production
    /// is HTTPS, so testing over HTTPS is also the faithful target.
    /// </summary>
    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "https://localhost:5003";

    /// <summary>
    /// Identity sent as <c>X-Fake-User</c> by <see cref="OpenAuthenticatedAsync"/>. Repositories
    /// are scoped per user, so assertions about chart contents only have something to look at
    /// when this names a user with analysed repositories — point it at one with
    /// <c>E2E_FAKE_USER</c>. The default is deliberately an isolated user: the shell, layout and
    /// theming tests are meaningful against an empty account, and the chart-content tests skip
    /// rather than fail when there is nothing to plot.
    /// </summary>
    public static string FakeUser =>
        Environment.GetEnvironmentVariable("E2E_FAKE_USER") ?? "e2e-ui";

    private IPlaywright? _playwright;
    public IBrowser? Browser { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _playwright = await Playwright.CreateAsync();
            Browser = await _playwright.Chromium.LaunchAsync();
        }
        catch (Exception)
        {
            Browser = null; // browsers not installed — every test skips
        }
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        _playwright?.Dispose();
    }

    /// <summary>
    /// Opens <paramref name="path"/> at the given viewport, skipping when the browser or the app
    /// is unavailable. The caller owns the returned page's context via <see cref="IAsyncDisposable"/>.
    /// </summary>
    /// <remarks>30 s for the document load: on a busy machine a cold WASM boot over the dev
    /// certificate overran the old 10 s and the run reported a timeout, not a defect.</remarks>
    public async Task<IPage> OpenAsync(ViewportSize viewport, string path = "/")
    {
        Skip.If(Browser is null, "Playwright browser unavailable — run playwright.ps1 install.");

        var context = await Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = viewport,
            // The local instance uses the ASP.NET dev certificate, which the browser does not trust.
            IgnoreHTTPSErrors = true
        });
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync(BaseUrl.TrimEnd('/') + path, new PageGotoOptions { Timeout = 30000 });
        }
        catch (PlaywrightException ex)
        {
            await context.DisposeAsync();
            throw new SkipException($"No app instance reachable at {BaseUrl} ({ex.Message}).");
        }

        return page;
    }

    /// <summary>
    /// Opens <paramref name="path"/> as a signed-in user. Authentication rides on the
    /// <c>X-Fake-User</c> header that FakeAuthHandler reads outside Production, applied at the
    /// browser-context level so the Blazor client's own fetch calls carry it too — the app
    /// resolves its auth state from an API call, not from the document request.
    /// <para>
    /// Everything behind [Authorize] — the repositories grid and every chart — is unreachable
    /// without this, which is why the existing suite only ever exercised /login.
    /// </para>
    /// </summary>
    public async Task<IPage> OpenAuthenticatedAsync(ViewportSize viewport, string path = "/", string? colorScheme = null, string? fakeUser = null)
    {
        Skip.If(Browser is null, "Playwright browser unavailable — run playwright.ps1 install.");

        var context = await Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = viewport,
            IgnoreHTTPSErrors = true,
            ColorScheme = colorScheme switch
            {
                "dark" => ColorScheme.Dark,
                "light" => ColorScheme.Light,
                _ => null
            },
            ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Fake-User"] = fakeUser ?? FakeUser }
        });

        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync(BaseUrl.TrimEnd('/') + path, new PageGotoOptions { Timeout = 30000 });
        }
        catch (PlaywrightException ex)
        {
            await context.DisposeAsync();
            throw new SkipException($"No app instance reachable at {BaseUrl} ({ex.Message}).");
        }

        return page;
    }
}

[CollectionDefinition(Name)]
public class E2EUiCollection : ICollectionFixture<E2EUiFixture>
{
    public const string Name = "E2E UI";
}
